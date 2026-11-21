using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace Win11Taskbar.Core;
public sealed class Settings : INotifyPropertyChanged
{
 public event PropertyChangedEventHandler? PropertyChanged;
 void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new(name));}
 private bool enabled=true;
 public bool Enabled {get=>enabled;set=>Set(ref enabled,value);}
 private bool startWithWindows=false;
 public bool StartWithWindows {get=>startWithWindows;set=>Set(ref startWithWindows,value);}
 private bool startMinimized=false;
 public bool StartMinimized {get=>startMinimized;set=>Set(ref startMinimized,value);}
 private bool minimizeToTray=true;
 public bool MinimizeToTray {get=>minimizeToTray;set=>Set(ref minimizeToTray,value);}
 private bool autoCheckUpdates=false;
 public bool AutoCheckUpdates {get=>autoCheckUpdates;set=>Set(ref autoCheckUpdates,value);}
 private string language="Русский";
 public string Language {get=>language;set=>Set(ref language,value);}
 private string theme="Dark";
 public string Theme {get=>theme;set=>Set(ref theme,value);}
 private string taskbarStyle="Floating";
 public string TaskbarStyle {get=>taskbarStyle;set=>Set(ref taskbarStyle,value);}
 private string alignment="Center";
 public string Alignment {get=>alignment;set=>Set(ref alignment,value);}
 private double opacity=0.9;
 public double Opacity {get=>opacity;set=>Set(ref opacity,value);}
 private double blurStrength=20;
 public double BlurStrength {get=>blurStrength;set=>Set(ref blurStrength,value);}
 private double cornerRadius=12;
 public double CornerRadius {get=>cornerRadius;set=>Set(ref cornerRadius,value);}
 private double taskbarHeight=48;
 public double TaskbarHeight {get=>taskbarHeight;set=>Set(ref taskbarHeight,value);}
 private double iconSize=24;
 public double IconSize {get=>iconSize;set=>Set(ref iconSize,value);}
 private double taskbarMargin=8;
 public double TaskbarMargin {get=>taskbarMargin;set=>Set(ref taskbarMargin,value);}
 private const string LegacyBackgroundColor = "#202536";
 private string backgroundColor = LegacyBackgroundColor;
 public string BackgroundColor
 {
  get => backgroundColor;
  set
  {
   bool followedSystem = FollowSystemTaskbarTheme;
   Set(ref backgroundColor, value);
   if (followedSystem != FollowSystemTaskbarTheme)
    PropertyChanged?.Invoke(this, new(nameof(FollowSystemTaskbarTheme)));
  }
 }
 private bool? followSystemTaskbarTheme;
 // Existing profiles with a custom tint keep that tint until the user switches back.
 public bool FollowSystemTaskbarTheme
 {
  get => followSystemTaskbarTheme ?? string.Equals(backgroundColor, LegacyBackgroundColor, StringComparison.OrdinalIgnoreCase);
  set => Set(ref followSystemTaskbarTheme, (bool?)value);
 }
 private string accentColor="#A69BFF";
 public string AccentColor {get=>accentColor;set=>Set(ref accentColor,value);}
 private double borderOpacity=0.15;
 public double BorderOpacity {get=>borderOpacity;set=>Set(ref borderOpacity,value);}
 private double shadowStrength=0.25;
 public double ShadowStrength {get=>shadowStrength;set=>Set(ref shadowStrength,value);}
 private bool showStart=true;
 public bool ShowStart {get=>showStart;set=>Set(ref showStart,value);}
 private bool showSearch=true;
 public bool ShowSearch {get=>showSearch;set=>Set(ref showSearch,value);}
 private bool showTaskView=false;
 public bool ShowTaskView {get=>showTaskView;set=>Set(ref showTaskView,value);}
 private bool showTray=true;
 public bool ShowTray {get=>showTray;set=>Set(ref showTray,value);}
 private bool showClock=true;
 public bool ShowClock {get=>showClock;set=>Set(ref showClock,value);}
 private bool allMonitors=true;
 public bool AllMonitors {get=>allMonitors;set=>Set(ref allMonitors,value);}
 private bool animationEnabled=true;
 public bool AnimationEnabled {get=>animationEnabled;set=>Set(ref animationEnabled,value);}
 private double animationDuration=250;
 public double AnimationDuration {get=>animationDuration;set=>Set(ref animationDuration,value);}
 private string animationType="Ease Out";
 public string AnimationType {get=>animationType;set=>Set(ref animationType,value);}
 private bool animateRepositioning=true;
 public bool AnimateRepositioning {get=>animateRepositioning;set=>Set(ref animateRepositioning,value);}
 private bool animateAppearance=true;
 public bool AnimateAppearance {get=>animateAppearance;set=>Set(ref animateAppearance,value);}
 private bool debugLogging=false;
 public bool DebugLogging {get=>debugLogging;set=>Set(ref debugLogging,value);}
 private bool experimentalEffects=true;
 public bool ExperimentalEffects {get=>experimentalEffects;set=>Set(ref experimentalEffects,value);}
 private bool onboardingComplete=false;
 public bool OnboardingComplete {get=>onboardingComplete;set=>Set(ref onboardingComplete,value);}
 private string updateEndpoint="";
 public string UpdateEndpoint {get=>updateEndpoint;set=>Set(ref updateEndpoint,value);}
 public void Validate()
 {
  static double Range(double value,double min,double max,double fallback)=>double.IsFinite(value)?Math.Clamp(value,min,max):fallback;
  Opacity=Range(Opacity,0,1,.9);CornerRadius=Range(CornerRadius,0,30,12);TaskbarHeight=Range(TaskbarHeight,32,64,48);TaskbarMargin=Range(TaskbarMargin,0,16,8);AnimationDuration=Range(AnimationDuration,0,1000,250);IconSize=Range(IconSize,16,32,24);BlurStrength=Range(BlurStrength,0,40,20);BorderOpacity=Range(BorderOpacity,0,1,.15);ShadowStrength=Range(ShadowStrength,0,1,.25);
  if(!new[]{"System","Light","Dark"}.Contains(Theme))Theme="Dark";
  if(!new[]{"Windows 11","Floating","Transparent","Acrylic","Solid"}.Contains(TaskbarStyle))TaskbarStyle="Floating";
  if(!new[]{"Left","Center","Dynamic Center"}.Contains(Alignment))Alignment="Center";
  if(!new[]{"Ease","Ease Out","Spring","Linear"}.Contains(AnimationType))AnimationType="Ease Out";
  if(!System.Text.RegularExpressions.Regex.IsMatch(BackgroundColor??"","^#[0-9a-fA-F]{6}$"))BackgroundColor="#202536";
  if(!System.Text.RegularExpressions.Regex.IsMatch(AccentColor??"","^#[0-9a-fA-F]{6}$"))AccentColor="#A69BFF";
  UpdateEndpoint??="";
 }
}
