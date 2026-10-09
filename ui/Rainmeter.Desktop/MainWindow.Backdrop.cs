using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private ICompositionSupportsSystemBackdrop? backdropTarget;
    private SystemBackdropConfiguration? backdropConfiguration;
    private MicaController? micaController;
    private DesktopAcrylicController? acrylicController;
    private bool micaAttached;
    private bool acrylicAttached;

    private void InitializeMaterialBackdrop()
    {
        DispatcherQueue.EnsureSystemDispatcherQueue();
        backdropTarget = this.As<ICompositionSupportsSystemBackdrop>();
        backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
        Activated += (_, args) =>
        {
            // Keep the transient acrylic material visible while another window has focus.
            if (backdropConfiguration is not null && !studioStyle)
                backdropConfiguration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        };
        Closed += (_, _) =>
        {
            DisposeMica();
            DisposeAcrylic();
            backdropConfiguration = null;
            backdropTarget = null;
        };
    }

    private void ApplyMaterialBackdrop()
    {
        if (backdropTarget is null || backdropConfiguration is null) return;

        backdropConfiguration.Theme = DarkTheme ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (studioStyle)
        {
            DisposeMica();
            backdropConfiguration.IsInputActive = true;
            if (!DesktopAcrylicController.IsSupported())
            {
                UseDefaultBackdrop();
                return;
            }

            SystemBackdrop = null;
            acrylicController ??= new DesktopAcrylicController();
            acrylicController.Kind = DesktopAcrylicKind.Thin;
            if (!acrylicAttached)
            {
                if (!acrylicController.AddSystemBackdropTarget(backdropTarget))
                {
                    DisposeAcrylic();
                    UseDefaultBackdrop();
                    return;
                }
                acrylicAttached = true;
            }
            acrylicController.SetSystemBackdropConfiguration(backdropConfiguration);
            acrylicController.TintColor = DarkTheme
                ? Color.FromArgb(255, 40, 31, 48)
                : Color.FromArgb(255, 247, 241, 251);
            acrylicController.FallbackColor = FallbackColor;
            acrylicController.TintOpacity = DarkTheme ? 0.18f : 0.15f;
            acrylicController.LuminosityOpacity = DarkTheme ? 0.33f : 0.41f;
        }
        else
        {
            DisposeAcrylic();
            if (!MicaController.IsSupported())
            {
                UseDefaultBackdrop();
                return;
            }

            SystemBackdrop = null;
            micaController ??= new MicaController();
            micaController.Kind = MicaKind.Base;
            if (!micaAttached)
            {
                if (!micaController.AddSystemBackdropTarget(backdropTarget))
                {
                    DisposeMica();
                    UseDefaultBackdrop();
                    return;
                }
                micaAttached = true;
            }
            micaController.SetSystemBackdropConfiguration(backdropConfiguration);
            micaController.TintColor = DarkTheme
                ? Color.FromArgb(255, 40, 31, 48)
                : Color.FromArgb(255, 247, 241, 251);
            micaController.FallbackColor = FallbackColor;
            micaController.TintOpacity = DarkTheme ? 0.30f : 0.27f;
            micaController.LuminosityOpacity = DarkTheme ? 0.87f : 0.92f;
        }
    }

    private Color FallbackColor => DarkTheme
        ? Color.FromArgb(255, 32, 26, 38)
        : Color.FromArgb(255, 248, 245, 251);

    private void UseDefaultBackdrop()
    {
        SystemBackdrop = studioStyle
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop { Kind = MicaKind.Base };
    }

    private void DisposeMica()
    {
        if (micaController is null) return;
        if (micaAttached) micaController.RemoveAllSystemBackdropTargets();
        micaController.Dispose();
        micaController = null;
        micaAttached = false;
    }

    private void DisposeAcrylic()
    {
        if (acrylicController is null) return;
        if (acrylicAttached) acrylicController.RemoveAllSystemBackdropTargets();
        acrylicController.Dispose();
        acrylicController = null;
        acrylicAttached = false;
    }
}
