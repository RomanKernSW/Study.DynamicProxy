﻿using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using FastProxy.Definitions;
using FastProxy.Helpers;
using static FastProxy.Extensions;

namespace FastProxy
{
    public sealed class ProxyMethodBuilder : IProxyMethodBuilder
    {
        #region statics
        private static MethodInfo EmptyTaskCall { get; } = typeof(Task).GetMethod(nameof(Task.FromResult), BindingFlags.Static | BindingFlags.Public).MakeGenericMethod(typeof(object));
        private static ConstructorInfo TaskWithResult { get; } = typeof(Task<object>).GetConstructor(new[] { typeof(Func<object, object>), typeof(object) });
        private static ConstructorInfo AnonymousFuncForTask { get; } = typeof(Func<object, object>).GetConstructor(new[] { typeof(object), typeof(IntPtr) });

        private static Type InterceptorValuesType { get; } = typeof(InterceptionInformation);
        private static ConstructorInfo InterceptorValuesConstructor { get; } = typeof(InterceptionInformation).GetConstructor(new[] { typeof(object), typeof(object), typeof(string), typeof(object[]), typeof(Task<object>) });
        private static MethodInfo InvokeInterceptor { get; } = typeof(IInterceptor).GetMethod(nameof(IInterceptor.Invoke));
        private static ConstructorInfo NewObject { get; } = typeof(object).GetConstructor(Type.EmptyTypes);
        #endregion

        public void Create(MethodInfo methodInfo, ProxyMethodBuilderTransientParameters transient)
        {
            var attributes = methodInfo.Attributes;
            if (transient.TypeInfo.ProxyType.IsInterface)
            {
                attributes = MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            }
            else if (methodInfo.IsAbstract)
            {
                attributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            }
            var parameters = methodInfo.GetParameters();
            var method = transient.TypeInfo.ProxyType.DefineMethod(methodInfo.Name, attributes, methodInfo.CallingConvention, methodInfo.ReturnType, parameters.Select(a => a.ParameterType).ToArray());
            MethodBuilder taskMethod = null;
            if (methodInfo.IsAbstract == false && (transient.TypeInfo.IsInterface == false || transient.TypeInfo.IsSealed))
            {
                taskMethod = CreateBaseCallForTask(method, parameters, transient);
            }
            if (methodInfo.ContainsGenericParameters)
            {
                //TODO
                throw new NotImplementedException();
            }
            var generator = method.GetILGenerator();
            var listType = typeof(object[]);
            //object[] items; {0}
            generator.DeclareLocal(listType);
            //Task<object> task; {1}
            generator.DeclareLocal(typeof(Task<object>));
            //InterceptorValues interceptorValues; {2}
            generator.DeclareLocal(InterceptorValuesType);

            foreach (var item in transient.PreInit)
            {
                item.Execute(transient.TypeInfo.ProxyType, transient.TypeInfo.Decorator, transient.TypeInfo.InterceptorInvoker, methodInfo, transient.PreInvoke, transient.PostInvoke);
            }
            generator.Emit(OpCodes.Ldc_I4, parameters.Length);
            generator.Emit(OpCodes.Newarr, typeof(object));
            generator.Emit(OpCodes.Stloc_0);
            for (var i = 0; i < parameters.Length; i++)
            {
                generator.Emit(OpCodes.Ldloc_0); //items.
                generator.Emit(OpCodes.Ldc_I4, i);
                generator.Emit(OpCodes.Ldarg, i + 1); // method arg by index i + 1 since ldarg_0 == this
                generator.Emit(OpCodes.Stelem_Ref); // items[x] = X;
            }

            if (taskMethod == null)
            {
                generator.Emit(OpCodes.Newobj, NewObject);
                generator.Emit(OpCodes.Call, EmptyTaskCall);
            }
            else
            {
                // new Task<object>([proxyMethod], items);
                generator.Emit(OpCodes.Ldarg_0); //this
                generator.Emit(OpCodes.Ldftn, taskMethod);
                generator.Emit(OpCodes.Newobj, AnonymousFuncForTask);
                generator.Emit(OpCodes.Ldloc_0); // load items
                generator.Emit(OpCodes.Newobj, TaskWithResult);
            }
            //task = {see above}
            generator.Emit(OpCodes.Stloc_1);

            foreach (var item in transient.PreInvoke)
            {
                item.Execute(transient.TypeInfo.ProxyType, transient.TypeInfo.Decorator, transient.TypeInfo.InterceptorInvoker, methodInfo, transient.PostInvoke);
            }

            //interceptorValues = new InterceptorValues(this, [null|decorator], "[MethodName]", items, task);
            generator.Emit(OpCodes.Ldarg_0);
            if (transient.TypeInfo.Decorator != null)
            {
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, transient.TypeInfo.Decorator);
            }
            else
            {
                generator.Emit(OpCodes.Ldnull);
            }
            generator.Emit(OpCodes.Ldstr, method.Name);
            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Ldloc_1);
            generator.Emit(OpCodes.Newobj, InterceptorValuesConstructor);
            generator.Emit(OpCodes.Stloc_2);

            //proxy.invoke(...)
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, transient.TypeInfo.InterceptorInvoker);
            generator.Emit(OpCodes.Ldloc_2);
            generator.Emit(OpCodes.Callvirt, InvokeInterceptor);


            foreach (var item in transient.PostInvoke)
            {
                item.Execute(transient.TypeInfo.ProxyType, transient.TypeInfo.Decorator, transient.TypeInfo.InterceptorInvoker, methodInfo);
            }

            if (method.ReturnType == typeof(void))
            {
                generator.Emit(OpCodes.Pop);
            }
            else
            {
                generator.Emit(method.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.ReturnType);
            }
            EmitDefaultValue(method.ReturnType, generator);

            generator.Emit(OpCodes.Ret);
            transient.MethodCreationCounter++;
        }

        private MethodBuilder CreateBaseCallForTask(MethodInfo baseMethod, ParameterInfo[] parameters, ProxyMethodBuilderTransientParameters transient)
        {
            var result = transient.TypeInfo.ProxyType.DefineMethod(string.Concat(baseMethod.Name, "_Execute_", transient.MethodCreationCounter), MethodAttributes.Private | MethodAttributes.HideBySig, typeof(object), new[] { typeof(object) });

            var generator = result.GetILGenerator();

            var items = generator.DeclareLocal(typeof(object[]));
            if (parameters.Length > 0)
            {
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Castclass, items.LocalType);
                generator.Emit(OpCodes.Stloc_0);
            }
            var methodCall = OpCodes.Call;
            generator.Emit(OpCodes.Ldarg_0); //base.
            if (transient.TypeInfo.Decorator != null)
            {
                methodCall = OpCodes.Callvirt;
                generator.Emit(OpCodes.Ldfld, transient.TypeInfo.Decorator); //_decorator
            }
            generator.Emit(OpCodes.Ldloc_0); //items

            for (var i = 0; i < parameters.Length; i++)
            {
                generator.Emit(OpCodes.Ldc_I4, i);
                generator.Emit(OpCodes.Ldelem_Ref);
                if (parameters[i].ParameterType != typeof(object))
                {
                    var casting = parameters[i].ParameterType.GetTypeInfo().IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass;
                    generator.Emit(casting, parameters[i].ParameterType);
                }
            }
            //(params ...)
            generator.Emit(methodCall, baseMethod);
            generator.Emit(OpCodes.Ret);

            return result;
        }
    }
}
