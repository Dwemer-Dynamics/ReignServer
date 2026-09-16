using TaleWorlds.MountAndBlade;

namespace Bannerlord.NativePortraitRenderer
{
    public sealed class SubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            NativePortraitRenderJob.TryArm();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            NativePortraitRenderJob.Tick(dt);
        }

        protected override void OnSubModuleUnloaded()
        {
            NativePortraitRenderJob.FinalizeJob();
            base.OnSubModuleUnloaded();
        }
    }
}
