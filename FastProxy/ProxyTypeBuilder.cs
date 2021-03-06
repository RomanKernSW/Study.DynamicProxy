﻿using System;
using System.Linq;
#if (!NETSTANDARD1_6)
using System.Diagnostics.SymbolStore;
#endif
using System.Reflection;
using System.Reflection.Emit;
using FastProxy.Definitions;
using FastProxy.Helpers;

namespace FastProxy
{
    public sealed class ProxyTypeBuilder : IProxyTypeBuilder
    {
        private const string ProxyPrefix = "Proxy";
        private const string DecoratorName = "_decorator";
        private const string ProxyInvoker = "_proxyInvoker";

        public ProxyTypeBuilderTransientParameters Create(Type abstractType, Type concreteType, Type interceptorType, ModuleBuilder moduleBuilder, string postfix)
        {
            if (abstractType == null)
            {
                throw new MissingConstructionInformation(nameof(abstractType), MissingConstructionInformation.TypeDefintion.AbstractType);
            }
            if (concreteType == null)
            {
                throw new MissingConstructionInformation(nameof(concreteType), MissingConstructionInformation.TypeDefintion.ConcreteType);
            }
            if (interceptorType == null)
            {
                throw new MissingConstructionInformation(nameof(interceptorType), MissingConstructionInformation.TypeDefintion.InterceptorType);
            }
            if (moduleBuilder == null)
            {
                throw new MissingConstructionInformation(nameof(moduleBuilder), MissingConstructionInformation.TypeDefintion.ModuleBuilder);
            }
            var typeInfoImplemented = concreteType;
            var result = new ProxyTypeBuilderTransientParameters
            {
                IsInterface = typeInfoImplemented.IsInterface,
                IsSealed = typeInfoImplemented.IsSealed,
                IsAbstract = typeInfoImplemented.IsAbstract,
#if (!NETSTANDARD2_0)
                SymbolDocument = moduleBuilder.DefineDocument(abstractType.FullName + ".il", SymDocumentType.Text, SymLanguageType.ILAssembly, SymLanguageVendor.Microsoft)
#endif
            };
            var defaultConstructorImplemented = false;
            if (typeInfoImplemented.IsClass && typeInfoImplemented.IsSealed && abstractType == concreteType)
            {
                throw new InvalidOperationException("Not possible to create a proxy if base type is sealed");
            }

            if (typeInfoImplemented.IsClass)
            {
                //TODO: not only empty constructors
                if (typeInfoImplemented.IsSealed)
                {
                    CreateSealedType(abstractType, concreteType, interceptorType, moduleBuilder, result, postfix);
                    defaultConstructorImplemented = true;
                }
                else
                {
                    result.ProxyType = moduleBuilder.DefineType(string.Concat(ProxyPrefix, abstractType.Name, postfix), TypeAttributes.Public | TypeAttributes.Class, concreteType);
                    result.ProxyType.SetParent(abstractType);
                }
                if (concreteType.IsInterface)
                {
                    result.Methods = concreteType.GetMethods();
                    result.Properties = concreteType.GetProperties();
                }
                else
                {
                    var methods = abstractType.GetMethods().ToDictionary(a => a.Name);
                    var properties = abstractType.GetProperties().ToDictionary(a => a.Name);
                    result.Methods = concreteType.GetMethods().Where(a => methods.ContainsKey(a.Name)).ToArray();
                    result.Properties = concreteType.GetProperties().Where(a => properties.ContainsKey(a.Name)).ToArray();
                }
            }
            else if (typeInfoImplemented.IsInterface)
            {
                result.ProxyType = moduleBuilder.DefineType(string.Concat(ProxyPrefix, abstractType.Name, postfix), TypeAttributes.Public | TypeAttributes.Class);
                result.ProxyType.AddInterfaceImplementation(abstractType);
                result.Methods = abstractType.GetMethods();
                result.Properties = abstractType.GetProperties();
            }
            else
            {
                //TODO 
                throw new NotImplementedException();
            }

            if (defaultConstructorImplemented == false)
            {
                var constructor = result.ProxyType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
                var ilGenerator = constructor.GetILGenerator();
                CreateProxyInvokerInConstuctor(interceptorType, ilGenerator, result);
                ilGenerator.Emit(OpCodes.Ret);
            }
            return result;
        }

        private static void CreateSealedType(Type abstractType, Type concreteType, Type interceptorType, ModuleBuilder moduleBuilder, ProxyTypeBuilderTransientParameters result, string postfix)
        {
            if (abstractType.IsInterface)
            {
                result.ProxyType = moduleBuilder.DefineType(string.Concat(ProxyPrefix, abstractType.Name, postfix),
                    TypeAttributes.Public | TypeAttributes.Class);
                result.ProxyType.AddInterfaceImplementation(abstractType);
            }
            else
            {
                result.ProxyType = moduleBuilder.DefineType(string.Concat(ProxyPrefix, abstractType.Name, postfix),
                    TypeAttributes.Public | TypeAttributes.Class, abstractType);
            }
            result.Decorator = result.ProxyType.DefineField(DecoratorName, abstractType, FieldAttributes.InitOnly);

            var constructor = result.ProxyType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var ilGenerator = constructor.GetILGenerator();
            ilGenerator.Emit(OpCodes.Ldarg_0);
            ilGenerator.Emit(OpCodes.Newobj, concreteType.GetConstructor(Type.EmptyTypes));
            ilGenerator.Emit(OpCodes.Stfld, result.Decorator);
#if (!NETSTANDARD2_0)
            ilGenerator.MarkSequencePoint(result.SymbolDocument, 1, 1, 1, 100);
#endif
            CreateProxyInvokerInConstuctor(interceptorType, ilGenerator, result);
            ilGenerator.Emit(OpCodes.Ret);
#if (!NETSTANDARD2_0)
            ilGenerator.MarkSequencePoint(result.SymbolDocument, 2, 1, 1, 100);
#endif

            CreateConstructorWithDecoratorAsParameter(abstractType, interceptorType, result);
        }

        private static void CreateConstructorWithDecoratorAsParameter(Type abstractType, Type interceptorType, ProxyTypeBuilderTransientParameters result)
        {
            var constructorWithDecoratorAsParameter = result.ProxyType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { abstractType });
            var ilGenerator = constructorWithDecoratorAsParameter.GetILGenerator();

            ilGenerator.Emit(OpCodes.Ldarg_0);
            ilGenerator.Emit(OpCodes.Ldarg_1);
            ilGenerator.Emit(OpCodes.Stfld, result.Decorator);
            CreateProxyInvokerInConstuctor(interceptorType, ilGenerator, result);
            ilGenerator.Emit(OpCodes.Ret);
        }

        private static void CreateProxyInvokerInConstuctor(Type interceptorType, ILGenerator generator, ProxyTypeBuilderTransientParameters result)
        {
            if (result.InterceptorInvoker == null)
            {
                result.InterceptorInvoker = result.ProxyType.DefineField(ProxyInvoker, typeof(IInterceptor), FieldAttributes.InitOnly);
            }
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Newobj, interceptorType.GetConstructor(Type.EmptyTypes));
            generator.Emit(OpCodes.Stfld, result.InterceptorInvoker);
#if (!NETSTANDARD2_0)
            generator.MarkSequencePoint(result.SymbolDocument, 1, 1, 1, 100);
#endif
        }
    }
}
