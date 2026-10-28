// HashVista — Hash & Media Scanner — GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
namespace HashVista;
public static class Program {
 [STAThread] public static void Main(string[] args) {
  var app = new Application();
  app.DispatcherUnhandledException += (_,e) => { MessageBox.Show(e.Exception.Message,"HashVista — Error"); e.Handled=true; };
  app.Run(new MainWindow());
 }
}
public sealed partial class MainWindow : Window {
 readonly ObservableCollection<string> roots = new();
 readonly List<Control> setupControls = [];
 readonly Dictionary<string,CheckBox> algorithms = new();
 readonly CheckBox metadata = new(){Content="Media metadata (slow; optional)",IsChecked=false};
 readonly CheckBox fingerprints = new(){Content="Media fingerprints (slow; optional)",IsChecked=false};
 readonly CheckBox streamHashes = new(){Content="Video/audio stream SHA-256 (encoded)",IsChecked=false};
 readonly CheckBox recursive = new(){Content="Recursive folders",IsChecked=true};
 readonly CheckBox hidden = new(){Content="Include hidden"};
 readonly CheckBox system = new(){Content="Include system"};
 readonly CheckBox links = new(){Content="Follow links (loop-safe)"};
 readonly DataGrid results = new();
 readonly ListBox rootList = new();
 readonly ComboBox filter = new(){Width=140,ItemsSource=new[]{"All files","Images","Videos","Other"},SelectedIndex=0};
 readonly ComboBox copyAlgorithm = new(){Width=90,ItemsSource=new[]{"SHA256","SHA512","SHA1","MD5"},SelectedIndex=0};
 readonly TextBlock pageLabel = new();
 readonly TextBlock activity = new(){Text="Choose files or folders to begin.",TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBlock counts = new(){Text="No scan loaded",TextWrapping=TextWrapping.Wrap};
 readonly TextBlock scanLabel = new(){Text="No saved scan",TextTrimming=TextTrimming.CharacterEllipsis};
 readonly TextBox details = new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Background=Brushes.Transparent};
 readonly WrapPanel previews = new();
 readonly TextBlock previewLabel = new(){Text="Select an image or video to preview",TextWrapping=TextWrapping.Wrap};
 readonly Button start = new(){Content="Start new scan"};
 readonly Button resume = new(){Content="Resume / refresh scan",IsEnabled=false};
 readonly Button cancel = new(){Content="Cancel",IsEnabled=false};
 readonly Button export = new(){Content="Export all results",IsEnabled=false};
 readonly ProgressBar bar = new(){Height=5,IsIndeterminate=false};
 readonly MediaProcessor media = new();
 readonly Stopwatch elapsed = new();
 readonly DispatcherTimer timer = new(){Interval=TimeSpan.FromMilliseconds(900)};
 ScanStore? store;
 CancellationTokenSource? scanCancel, previewCancel;
 ScanProgress? progress;
 bool busy, exporting, closingAfterCancel;
 int offset, pageRefreshTicks;
 string emptySummary = "";
 const int PageSize=250;
 string Kind => filter.SelectedIndex switch {1=>"Image",2=>"Video",3=>"Other",_=>"All"};
 public MainWindow() {
  Title="HashVista — Hash & Media Scanner 1.3.0"; Width=1480; Height=950; MinWidth=1120; MinHeight=760; WindowStartupLocation=WindowStartupLocation.CenterScreen;
  FontFamily=new FontFamily("Segoe UI"); FontSize=13; Background=new SolidColorBrush(Color.FromRgb(244,247,251));
  var buttonStyle=new Style(typeof(Button)); buttonStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,7,12,7))); buttonStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,0,7,6))); buttonStyle.Setters.Add(new Setter(Control.MinHeightProperty,33d)); Resources.Add(typeof(Button),buttonStyle);
  var checkStyle=new Style(typeof(CheckBox)); checkStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,5,18,5))); Resources.Add(typeof(CheckBox),checkStyle);
  Content=BuildLayout();
  start.Click+=async (_,_)=>await StartAsync(); resume.Click+=async (_,_)=>await RunAsync(true); cancel.Click+=(_,_)=>{scanCancel?.Cancel(); cancel.IsEnabled=false; activity.Text="Cancelling — saving partial results…";}; export.Click+=async (_,_)=>await ExportAsync();
  filter.SelectionChanged+=(_,_)=>{offset=0;RefreshPage();};
  results.SelectionChanged+=async (_,_)=>{if(!refreshingRows)await SelectAsync();};
  timer.Tick+=(_,_)=>Tick(); timer.Start();
  AllowDrop=true; DragOver+=(_,e)=>{ e.Effects=!busy && e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None; e.Handled=true; };
  Drop+=(_,e)=>{if(!busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)AddRoots(paths);};
  Closing+=(_,e)=>{if(busy){ e.Cancel=true;closingAfterCancel=true;scanCancel?.Cancel();activity.Text=exporting?"Finishing export before closing…":"Finishing cancellation before closing…";}else{previewCancel?.Cancel();store?.Dispose();}};
 }
 Button Button(string label,Action action,bool setup=false){var b=new Button{Content=label};b.Click+=(_,_)=>action();if(setup)setupControls.Add(b);return b;}
 void AddRoots(IEnumerable<string> paths){foreach(var p in paths){var full=ScanStore.NormalizePath(p);if(!roots.Contains(full,StringComparer.OrdinalIgnoreCase))roots.Add(full);}}
 void AddFiles(){var dlg=new OpenFileDialog{Title="Add files",Multiselect=true,CheckFileExists=true};if(dlg.ShowDialog(this)==true)AddRoots(dlg.FileNames);}
 void AddFolder(){var dlg=new OpenFolderDialog{Title="Add folder",Multiselect=true};if(dlg.ShowDialog(this)==true)AddRoots(dlg.FolderNames);}
 ScanOptions ReadOptions()=>new(){Algorithms=algorithms.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray(),Metadata=metadata.IsChecked==true,Fingerprints=fingerprints.IsChecked==true,StreamHashes=streamHashes.IsChecked==true,Recursive=recursive.IsChecked==true,IncludeHidden=hidden.IsChecked==true,IncludeSystem=system.IsChecked==true,FollowLinks=links.IsChecked==true};
 void SetOptions(ScanOptions o){foreach(var x in algorithms)x.Value.IsChecked=o.Algorithms.Contains(x.Key);metadata.IsChecked=o.Metadata;fingerprints.IsChecked=o.Fingerprints;streamHashes.IsChecked=o.StreamHashes;recursive.IsChecked=o.Recursive;hidden.IsChecked=o.IncludeHidden;system.IsChecked=o.IncludeSystem;links.IsChecked=o.FollowLinks;}
 async Task StartAsync(){
  if(busy)return;if(roots.Count==0){MessageBox.Show(this,"Choose files or a folder first.","HashVista");return;}
  var options=ReadOptions();if(options.Algorithms.Length==0&&!options.Metadata&&!options.Fingerprints&&!options.StreamHashes){MessageBox.Show(this,"Select at least one processing stage.","HashVista");return;}
  try{
   string data=Path.Combine(AppContext.BaseDirectory,"Data","scans");Directory.CreateDirectory(data);
   var dlg=new SaveFileDialog{Title="Save new scan database",Filter="HashVista scan (*.ghscan)|*.ghscan",FileName="scan-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".ghscan",InitialDirectory=data,OverwritePrompt=true};
   if(dlg.ShowDialog(this)!=true)return;
   if(File.Exists(dlg.FileName)){MessageBox.Show(this,"Choose a new database filename. Open saved scan to resume an existing database.","HashVista");return;}
   previewCancel?.Cancel();store?.Dispose();store=new ScanStore(dlg.FileName);store.Create(roots.ToArray(),options);offset=0;await RunAsync(false);
  }catch(Exception ex){ShowError(ex);}
 }
 async Task RunAsync(bool isResume){
  if(busy||store==null)return; if(isResume)SetOptions(store.Info.Options);
  previewCancel?.Cancel();previews.Children.Clear();previewLabel.Text="Preview available after the scan finishes or is cancelled.";busy=true;scanCancel=new();progress=null;elapsed.Restart();pageRefreshTicks=0;scanControl=new();lastRateSeconds=0;lastRateBytes=0;currentRate=0;SetBusy();
  activity.Text=isResume?"Preparing a fresh traversal…":"Preparing file inventory…";
  try{
   var o=store.Info.Options;
   if(o.Metadata||o.Fingerprints||o.StreamHashes){activity.Text="Checking FFmpeg / FFprobe / ExifTool…";store.Info.ToolVersions=await media.GetVersionsAsync(scanCancel.Token);}
   else store.Info.ToolVersions="Media stages disabled";
   store.SaveInfo();activity.Text="Starting file inventory…";
   var engine=new ScanEngine(store,media);await engine.RunAsync(isResume,new LatestProgress<ScanProgress>(p=>progress=p),scanCancel.Token,scanControl);
  }
  catch(OperationCanceledException){activity.Text="Cancelled — partial results saved.";}
  catch(Exception ex){try{store.Info.Status="Interrupted";store.SaveInfo();}catch{}ShowError(ex);}
  finally{busy=false;elapsed.Stop();scanControl?.Dispose();scanControl=null;scanCancel.Dispose();scanCancel=null;SetBusy();RefreshPage();UpdateEmptySummary();Tick();if(closingAfterCancel)Close();}
 }
 void SetBusy(){foreach(var c in setupControls)c.IsEnabled=!busy;start.IsEnabled=!busy;resume.IsEnabled=!busy&&store!=null;cancel.IsEnabled=busy&&!exporting;export.IsEnabled=!busy&&store!=null;exportFiltered.IsEnabled=!busy&&store!=null;UpdatePauseState();bar.Minimum=0;bar.Maximum=100;bar.IsIndeterminate=busy&&(exporting||store==null||store.Info.Status is "Queued" or "Enumerating" or "Resuming");}
 void OpenScan(){
  var dlg=new OpenFileDialog{Title="Open saved scan",Filter="HashVista scans (*.ghscan;*.db)|*.ghscan;*.db|All files|*.*"};if(dlg.ShowDialog(this)!=true)return;
  try{var next=new ScanStore(dlg.FileName,requireExisting:true);var info=next.Info;previewCancel?.Cancel();store?.Dispose();store=next;roots.Clear();AddRoots(info.Roots);SetOptions(info.Options);offset=0;progress=null;if(info.Status is "Running" or "Enumerating" or "Processing"){info.Status="Interrupted";store.SaveInfo();}SetBusy();RefreshPage();UpdateEmptySummary();Tick();if(info.TotalFiles>0)activity.Text="Saved results loaded. Resume / refresh scan recomputes files with the saved configuration.";}
  catch(Exception ex){ShowError(ex);}
 }
 void Tick(){
  if(store==null)return;var info=store.Info;
  if(info.Status=="Resuming"){bar.IsIndeterminate=busy;activity.Text="Invalidating previous results before a fresh traversal…";return;}if(exporting){bar.IsIndeterminate=true;return;}
  var p=progress;
  scanLabel.Text=$"Scan {info.ScanId}  |  {info.Status}  |  {store.DatabasePath}";scanLabel.ToolTip=scanLabel.Text;
  bool inventoryPhase=info.Status is "Queued" or "Enumerating" or "Resuming";
  bar.IsIndeterminate=busy&&inventoryPhase;
  if(p!=null){
   if(!bar.IsIndeterminate)bar.Value=Math.Clamp(p.OverallPercentage,0,100);
   if(inventoryPhase)counts.Text=$"INVENTORY  •  Files {p.Discovered:N0}  •  Images {info.ImageFiles:N0} ({FormatBytes(info.ImageBytes)})  •  Videos {info.VideoFiles:N0} ({FormatBytes(info.VideoBytes)})  •  Other {info.OtherFiles:N0} ({FormatBytes(info.OtherBytes)})  •  Total {FormatBytes(info.TotalBytes)}  •  Elapsed {elapsed.Elapsed:hh\\:mm\\:ss}";
   else counts.Text=$"{(scanControl?.IsPaused==true?"PAUSED":busy?"PROCESSING":"FINISHED")}  •  Overall {p.OverallPercentage:F1}%  •  {FormatBytes(p.HandledBytes)} / {FormatBytes(p.TotalBytes)}  •  Files {p.Processed:N0}/{p.TotalFiles:N0}  •  Failed {p.Failed:N0}  •  Skipped {p.Skipped:N0}  •  Actually read {FormatBytes(p.BytesProcessed)}  •  Elapsed {elapsed.Elapsed:hh\\:mm\\:ss}";
   activity.Text=busy?p.Activity:$"{info.Status} — saved results are available for review and export.";
  }else{
   if(!busy)bar.Value=info.Status is "Completed" or "CompletedWithIssues"?100:0;
   counts.Text=$"Stored files {info.TotalFiles:N0} ({FormatBytes(info.TotalBytes)})  •  Images {info.ImageFiles:N0}  •  Videos {info.VideoFiles:N0}  •  Other {info.OtherFiles:N0}  •  Status {info.Status}  •  Inventory complete {info.EnumerationComplete}";
  }
  if(!busy && info.TotalFiles==0){bar.IsIndeterminate=false;bar.Value=0;counts.Text=$"NO FILES SCANNED  •  Stored files 0  •  Status {info.Status}";activity.Text=emptySummary;activity.ToolTip=emptySummary;previewLabel.Text=emptySummary;}
  UpdatePauseState();UpdateRate();
  if(busy && ++pageRefreshTicks>=3){pageRefreshTicks=0;RefreshPage();}
 }
 void UpdateEmptySummary(){
  emptySummary="";activity.ToolTip=null;
  if(store==null||store.Info.TotalFiles!=0)return;
  var issue=store.Issues().FirstOrDefault();
  emptySummary=issue==null?"No files found in the selected locations. Check the input paths and scan options.":$"No files were scanned. {issue.Code}: {issue.Path}. {issue.Message} Open Traversal issues for details.";
 }
 // Paging uses the stored row count; inventory counters can be partial while resuming.
 static string FormatBytes(long bytes){double value=Math.Max(0,bytes);string[] units=["B","KiB","MiB","GiB","TiB","PiB"];int unit=0;while(value>=1024&&unit<units.Length-1){value/=1024;unit++;}return $"{value:0.##} {units[unit]}";}
 async Task SelectAsync(){
  previewCancel?.Cancel();previewCancel?.Dispose();previewCancel=new();var token=previewCancel.Token;previews.Children.Clear();
  if(results.SelectedItem is not FileResult row){details.Text="";previewLabel.Text="Select an image or video to preview";return;}
  var sb=new StringBuilder();sb.AppendLine(row.FullPath).AppendLine().AppendLine($"Disk serial: {row.DiskSerialNumber}").AppendLine($"Disk number: {row.DiskNumber}; Volume: {row.VolumeRoot}; Serial status: {row.DiskSerialStatus}").AppendLine().AppendLine($"File ID: {row.FileId}").AppendLine($"Status: {row.ScanStatus}").AppendLine($"Hash: {row.HashStatus}; Metadata: {row.MetadataStatus}; Fingerprint: {row.FingerprintStatus}").AppendLine($"Stream hash: {row.StreamHashStatus}").AppendLine($"Size: {row.SizeBytes?.ToString()??"Unknown"}");
  foreach(var h in row.Hashes)sb.AppendLine().AppendLine(h.Key).AppendLine(h.Value);
  foreach(var m in row.Media)sb.AppendLine($"{m.Key}: {m.Value}");
  if(row.Frames.Count>0)sb.AppendLine().AppendLine("Frame samples").AppendLine(JsonSerializer.Serialize(row.Frames,new JsonSerializerOptions{WriteIndented=true}));
  if(row.Streams.Count>0)sb.AppendLine().AppendLine("Streams").AppendLine(JsonSerializer.Serialize(row.Streams,new JsonSerializerOptions{WriteIndented=true}));
  if(row.StreamHashes.Count>0)sb.AppendLine().AppendLine("Stream hashes").AppendLine(JsonSerializer.Serialize(row.StreamHashes,new JsonSerializerOptions{WriteIndented=true}));
  if(row.Errors.Count>0)sb.AppendLine().AppendLine("Errors").AppendLine(row.ErrorMessage);
  sb.AppendLine().AppendLine($"Computed UTC: {row.ComputedUtc:O}").AppendLine($"Provenance: {row.Provenance}").AppendLine("Configuration: "+row.ConfigurationJson);details.Text=sb.ToString();
  if(row.Kind=="Other"){previewLabel.Text="File details";return;} if(busy){previewLabel.Text="Preview available after the scan finishes or is cancelled.";return;}
  previewLabel.Text="Loading selected media preview…";
  try{var images=await media.PreviewAsync(row,token);token.ThrowIfCancellationRequested();if(!busy && store?.Info.ScanId==row.ScanId){row.Media["PreviewComputedUtc"]=DateTime.UtcNow.ToString("O");row.Media["PreviewBasis"]="Current file on selection; not a filesystem snapshot";store.SaveResult(row);}foreach(var bytes in images){var bmp=new BitmapImage();using var stream=new MemoryStream(bytes);bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.StreamSource=stream;bmp.EndInit();bmp.Freeze();previews.Children.Add(new Image{Source=bmp,Width=images.Length>1?145:300,Height=images.Length>1?90:160,Stretch=Stretch.Uniform,Margin=new Thickness(2)});}previewLabel.Text=images.Length>0?(row.Kind=="Video"?"Sampled video frames — lazy preview":"Selected image — lazy preview"):"No preview available";}
  catch(OperationCanceledException){}
  catch(Exception ex){if(!token.IsCancellationRequested)previewLabel.Text="Preview unavailable: "+ex.Message;}
 }
 async Task ExportAsync(bool filtered=false){
  if(store==null||busy)return;
  var dlg=new SaveFileDialog{Title=filtered?"Export filtered results — CSV":"Export all results — CSV",Filter="CSV UTF-8 (*.csv)|*.csv",FileName="hashvista-"+store.Info.ScanId[..8]+".csv",OverwritePrompt=true};if(dlg.ShowDialog(this)!=true)return;
  var exportingStore=store;var exportScope=filtered?CurrentFilter:ResultFilter.All;previewCancel?.Cancel();busy=true;exporting=true;SetBusy();activity.Text=filtered?"Exporting filtered stored results…":"Exporting all stored results…";
  try{long count=await Task.Run(()=>CsvExporter.Export(exportingStore,dlg.FileName,exportScope));activity.Text=$"Exported {count:N0} stored file rows to {dlg.FileName}";if(!closingAfterCancel)MessageBox.Show(this,$"Exported {count:N0} files.\nScan: {exportingStore.Info.Status}\n{dlg.FileName}\n\nImport hash columns as text in spreadsheet applications.","CSV saved");}
  catch(Exception ex){ShowError(ex);}
  finally{busy=false;exporting=false;SetBusy();if(closingAfterCancel)Close();}
 }
 void CopyHash(){if(results.SelectedItem is FileResult row && copyAlgorithm.SelectedItem is string name && row.Hashes.TryGetValue(name,out string? hash))Clipboard.SetText(hash);}
 void CopyRows(){if(store==null)return;var selected=results.SelectedItems.OfType<FileResult>().ToList();if(selected.Count>0)Clipboard.SetText(CsvExporter.ClipboardText(selected,store.Info));}
 void ShowIssues(){if(store==null)return;var window=new Window{Owner=this,Title=$"Traversal issues / exclusions — first 1,000 of {store.IssueCount():N0}; full list in CSV .scan.json",Width=950,Height=500,WindowStartupLocation=WindowStartupLocation.CenterOwner};window.Content=new DataGrid{ItemsSource=store.Issues().Take(1000).ToList(),IsReadOnly=true,AutoGenerateColumns=true,CanUserAddRows=false,CanUserDeleteRows=false};window.ShowDialog();}
 void ShowTools(){var text=media.DependencyStatus()+"\n\nFull-file hashes use .NET cryptography. Media stages run locally.\n\nResume / refresh always recomputes all files with the saved configuration. No cached digest is reported as fresh.\n\nSee README_AR.md, BUILD.md and MEDIA_PROFILES.md in this portable folder for decoder and fingerprint profiles.\n\nRAW fingerprints use an embedded preview where available. Unsupported decoding does not prevent ordinary file hashing.";var w=new Window{Owner=this,Title="Tools & profiles",Width=750,Height=420,WindowStartupLocation=WindowStartupLocation.CenterOwner};w.Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(18)};w.ShowDialog();}
 void ShowError(Exception ex){activity.Text=ex.Message;MessageBox.Show(this,ex.Message,"HashVista — Error",MessageBoxButton.OK,MessageBoxImage.Error);}
}

internal sealed class LatestProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value)=>report(value); }




