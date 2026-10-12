using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;

namespace TunicCoop
{
    /// <summary>
    /// Code-level hooks on IL2CPP methods. In this BepInEx 6 pre.1 setup, Harmony patches install but never
    /// fire for game-internal calls (verified on Chest.Interact and the open coroutine's MoveNext): the patch
    /// only affects calls routed through il2cpp_runtime_invoke. A NativeDetour rewrites the function's machine
    /// code, so every caller (direct, vtable, interface, coroutine scheduler) goes through the hook.
    ///
    /// IL2CPP x64 signature for an instance method: ret fn(IntPtr this, ...args, IntPtr methodInfo).
    /// </summary>
    public static class NativeHook
    {
        static readonly List<object> _keepAlive = new List<object>();

        /// <summary>Native code address of an interop method, via its NativeMethodInfoPtr_* field.</summary>
        public static unsafe IntPtr MethodPtr(Type interopType, string nativeField)
        {
            RuntimeHelpers.RunClassConstructor(interopType.TypeHandle);
            var f = interopType.GetField(nativeField, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null)
            {
                // Other store builds may generate a slightly different name (visibility/overload suffix):
                // fall back to the method-name prefix, if it is unique.
                string method = nativeField.Replace("NativeMethodInfoPtr_", "");
                foreach (var cut in new[] { "_Public_", "_Private_", "_Protected_", "_Internal_" })
                {
                    int i = method.IndexOf(cut, StringComparison.Ordinal);
                    if (i > 0) { method = method.Substring(0, i); break; }
                }
                FieldInfo only = null; int hits = 0;
                foreach (var cand in interopType.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                    if (cand.Name.StartsWith("NativeMethodInfoPtr_" + method + "_", StringComparison.Ordinal)) { only = cand; hits++; }
                if (hits != 1) throw new MissingFieldException(interopType.Name, $"{nativeField} (prefix matches: {hits})");
                Plugin.L.LogWarning($"[hook] {interopType.Name}: using {only.Name} for {nativeField}");
                f = only;
            }
            var methodInfo = (IntPtr)f.GetValue(null);
            if (methodInfo == IntPtr.Zero) throw new InvalidOperationException($"{interopType.Name}.{nativeField} is null");
            return *(IntPtr*)methodInfo; // Il2CppMethodInfo.methodPointer is the first field
        }

        /// <summary>Detour target → hook. Returns a trampoline that calls the original.</summary>
        public static T Install<T>(string name, IntPtr target, T hook) where T : Delegate
        {
            _keepAlive.Add(hook); // the GC must never collect a delegate native code jumps into
            var det = new NativeDetour(target, Marshal.GetFunctionPointerForDelegate(hook));
            _keepAlive.Add(det);
            var orig = det.GenerateTrampoline<T>();
            _keepAlive.Add(orig);
            Plugin.L.LogInfo($"[hook] {name} @ 0x{target.ToInt64():X}");
            return orig;
        }
    }
}
