using System;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using muxc = Microsoft.UI.Xaml.Controls;

namespace TranslatorApp
{
 public static class ThemeHelper
 {
 public static void ApplySavedThemeAndMaterial()
 {
 var settings = ApplicationData.Current.LocalSettings;
 var theme = (settings.Values["App_Theme"] as string) ?? "Default";
 var material = (settings.Values["App_Material"] as string) ?? "Mica";

 // Apply theme to root control
 var root = Window.Current.Content as Control;
 if (root != null)
 {
 switch (theme)
 {
 case "Light": root.RequestedTheme = ElementTheme.Light; break;
 case "Dark": root.RequestedTheme = ElementTheme.Dark; break;
 default: root.RequestedTheme = ElementTheme.Default; break;
 }

 // Apply material
 try { muxc.BackdropMaterial.SetApplyToRootOrPageBackground(root, false); } catch { }

 if (material == "Mica")
 {
 root.ClearValue(Control.BackgroundProperty);
 try { muxc.BackdropMaterial.SetApplyToRootOrPageBackground(root, true); } catch { }
 }
 else if (material == "Acrylic")
 {
 var tint = (root.RequestedTheme == ElementTheme.Dark) ? Colors.Black : Colors.White;
 root.Background = new AcrylicBrush
 {
 BackgroundSource = AcrylicBackgroundSource.HostBackdrop,
 TintColor = tint,
 TintOpacity =0.6,
 FallbackColor = Colors.Gray
 };
 }
 else // None
 {
 var color = (root.RequestedTheme == ElementTheme.Dark) ? Colors.Black : Colors.White;
 root.Background = new SolidColorBrush(color);
 }
 }
 }
 }
}
