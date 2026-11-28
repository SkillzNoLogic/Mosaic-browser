using System.Runtime.CompilerServices;
using Mosaic.Core.Browser;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>Plugs the system area in. Only stores delegates: nothing runs until it is used.</summary>
internal static class SystemMounts
{
    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.Startup.Add(("system", SystemIntegration.Start));
        UserNotifications.PresenterFactory = () => AppNotificationPresenter.Create(SystemIntegration.Dispatcher, SystemIntegration.ShowNotice);
        SystemAppearanceMonitor.Starter = AppearanceListener.Start;
        AutomationRegistry.Register(SystemIntegration.HandleAutomationAsync);
        AutomationRegistry.RegisterState("system", SystemIntegration.State);
    }
}
