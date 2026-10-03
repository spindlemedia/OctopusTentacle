#if !NETFRAMEWORK
using System;
using Autofac;
using Octopus.Tentacle.Core.Services.Scripts;

namespace Octopus.Tentacle.Sandbox
{
    public class AcaSandboxModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            if (!AcaSandboxConfiguration.IsEnabled)
                return;

            builder.Register(_ => AcaSandboxConfiguration.FromEnvironment()).SingleInstance();
            builder.RegisterType<AcaSandboxTokenProvider>().SingleInstance();
            builder.RegisterType<AcaSandboxClient>().SingleInstance();
            builder.RegisterType<AcaSandboxBlobStore>().SingleInstance();
            builder.RegisterType<AcaSandboxPodImages>().SingleInstance();
            builder.RegisterType<AcaSandboxToolSnapshots>().SingleInstance();
            builder.RegisterType<AcaSandboxScriptRunner>().As<IScriptRunner>().SingleInstance();
        }
    }
}
#endif
