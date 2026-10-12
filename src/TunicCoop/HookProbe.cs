using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TunicCoop
{
    /// <summary>Diagnostics: native hooks that only count calls, to prove which game paths run.</summary>
    public static class HookProbe
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn(IntPtr self, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte InteractFn(IntPtr self, IntPtr item, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PtrFn(IntPtr self, IntPtr mi);

        static VoidFn _origUpdate; static InteractFn _origInteract; static PtrFn _origOpenSeq;
        public static int UpdateCalls, InteractCalls, OpenSeqCalls, LastChestId = -1;

        public static void Install()
        {
            try
            {
                _origUpdate = NativeHook.Install<VoidFn>("PlayerCharacter.Update",
                    NativeHook.MethodPtr(typeof(PlayerCharacter), "NativeMethodInfoPtr_Update_Protected_Void_0"),
                    (s, m) => { Interlocked.Increment(ref UpdateCalls); _origUpdate(s, m); });
                _origInteract = NativeHook.Install<InteractFn>("Chest.Interact",
                    NativeHook.MethodPtr(typeof(Chest), "NativeMethodInfoPtr_IInteractionReceiver_Interact_Private_Virtual_Final_New_Boolean_Item_0"),
                    (s, i, m) =>
                    {
                        Interlocked.Increment(ref InteractCalls);
                        try { LastChestId = new Chest(s).chestID; } catch { }
                        return _origInteract(s, i, m);
                    });
                _origOpenSeq = NativeHook.Install<PtrFn>("Chest.openSequence",
                    NativeHook.MethodPtr(typeof(Chest), "NativeMethodInfoPtr_openSequence_Protected_IEnumerator_0"),
                    (s, m) =>
                    {
                        Interlocked.Increment(ref OpenSeqCalls);
                        try { int id = new Chest(s).chestID; LastChestId = id; ChestSync.Opened(id, "openSequence()"); } catch { }
                        return _origOpenSeq(s, m);
                    });
            }
            catch (Exception e) { Plugin.L.LogError("[probe] install failed: " + e); }
        }

        public static string Stats() => $"update={UpdateCalls} interact={InteractCalls} openSeq={OpenSeqCalls} lastChest={LastChestId}";
    }
}
