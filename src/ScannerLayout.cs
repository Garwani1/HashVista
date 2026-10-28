// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace HashVista;

public sealed partial class MainWindow
{
    readonly Button pause = new() { Content = "Pause", IsEnabled = false };
    readonly Button exportFiltered = new() { Content = "Export filtered results", IsEnabled = false };
    readonly ComboBox diskFilter = new() { MinWidth = 125, MaxWidth = 210, Margin = new Thickness(6,0,6,6) };
    readonly TextBlock rateLabel = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0,3,0,3) };
    ScanControl? scanControl;
    bool refreshingRows, pageLoading, pageQueued;
    long pageTotal;
    double lastRateSeconds;
    long lastRateBytes;
    double currentRate;
    string? displayedScan;
    string displayedKind = "All";
    string? displayedDisk;
    int displayedOffset = -1;
    sealed record DiskChoice(string Label, string? Number) { public override string ToString() => Label; }
    ResultFilter CurrentFilter => new(Kind, (diskFilter.SelectedItem as DiskChoice)?.Number);

    UIElement BuildLayout()
    {
        var outer = new Grid();
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new StackPanel { Margin = new Thickness(20,12,20,12) };
        title.Children.Add(new TextBlock { Text = "HASHVISTA", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        title.Children.Add(new TextBlock { Text = "Hash & Media Scanner  /  File inventory · Media inspection · CSV", Foreground = Brushes.LightSteelBlue });
        outer.Children.Add(new Border { Child = title, Background = new SolidColorBrush(Color.FromRgb(19,39,61)) });

        var options = new StackPanel { Margin = new Thickness(18,10,18,8) };
        var hashes = new WrapPanel();
        hashes.Children.Add(new TextBlock { Text = "Full-file hashes", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,6,18,0) });
        foreach (var name in new[] { "SHA256", "SHA512", "SHA1", "MD5" })
        {
            var cb = new CheckBox { Content = name, IsChecked = name == "SHA256" };
            algorithms[name] = cb; hashes.Children.Add(cb); setupControls.Add(cb);
            cb.Checked += (_,_) => UpdateHashColumns(); cb.Unchecked += (_,_) => UpdateHashColumns();
        }
        hashes.Children.Add(new TextBlock { Text = "One file read for all selected hashes", Foreground = Brushes.DimGray, Margin = new Thickness(8,6,12,0) });
        hashes.Children.Add(Button("Tools & profiles", ShowTools)); options.Children.Add(hashes);
        var extra = new StackPanel();
        var mediaOptions = new WrapPanel();
        foreach (var cb in new[] { metadata, fingerprints, streamHashes }) mediaOptions.Children.Add(cb);
        extra.Children.Add(mediaOptions);
        extra.Children.Add(new TextBlock { Text = "Optional media stages need local FFmpeg / FFprobe / ExifTool. They add work to each supported file.", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        options.Children.Add(new Expander { Header = "Media options", Content = extra });
        var traversal = new WrapPanel();
        foreach (var cb in new[] { recursive, hidden, system, links }) traversal.Children.Add(cb);
        options.Children.Add(traversal);
        setupControls.AddRange(new[] { metadata, fingerprints, streamHashes, recursive, hidden, system, links });
        Grid.SetRow(options,1); outer.Children.Add(options);

        var workspace = new Grid { Margin = new Thickness(18,0,18,10) };
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(310), MinWidth = 270 });
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star), MinWidth = 600 });
        var source = new Grid();
        source.RowDefinitions.Add(new() { Height = GridLength.Auto });
        source.RowDefinitions.Add(new() { Height = new GridLength(1,GridUnitType.Star) });
        source.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var add = new StackPanel();
        add.Children.Add(Section("1  Choose files & folders"));
        var picks = new WrapPanel(); picks.Children.Add(Button("Add files",AddFiles,true)); picks.Children.Add(Button("Add folder",AddFolder,true)); add.Children.Add(picks);
        add.Children.Add(new TextBlock { Text = "Check a folder to add it. Expand only the locations you need.", Foreground=Brushes.DimGray, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,6,7) });
        source.Children.Add(add);
        var tabs = new TabControl();
        var browser = new FolderBrowser(roots, p => AddRoots(new[] { p }));
        setupControls.Add(browser);
        tabs.Items.Add(new TabItem { Header = "Browse", Content = browser });
        var selected = new DockPanel();
        var remove = new WrapPanel();
        remove.Children.Add(Button("Remove selected",()=> { foreach(var p in rootList.SelectedItems.Cast<string>().ToArray()) roots.Remove(p); },true));
        remove.Children.Add(Button("Clear input list",()=>roots.Clear(),true));
        DockPanel.SetDock(remove,Dock.Bottom); selected.Children.Add(remove);
        rootList.ItemsSource=roots; rootList.SelectionMode=SelectionMode.Extended;
        AutomationProperties.SetName(rootList,"Selected files and folders"); selected.Children.Add(rootList);
        var selectedTab = new TabItem { Header = "Selected (0)", Content = selected };
        roots.CollectionChanged += (_,_) => selectedTab.Header = $"Selected ({roots.Count})";
        tabs.Items.Add(selectedTab); Grid.SetRow(tabs,1); source.Children.Add(tabs);
        var controls = new StackPanel { Margin = new Thickness(0,12,0,0) };
        controls.Children.Add(Section("2  Scan"));
        var commands = new WrapPanel();
        commands.Children.Add(Button("Open saved scan",OpenScan,true)); commands.Children.Add(start);
        commands.Children.Add(pause); commands.Children.Add(cancel); commands.Children.Add(resume);
        commands.Children.Add(Button("Traversal issues",ShowIssues,true)); controls.Children.Add(commands);
        controls.Children.Add(new TextBlock { Text = "Continue keeps the current pass. Resume / refresh recomputes a saved scan.", TextWrapping=TextWrapping.Wrap, Foreground=Brushes.DimGray, Margin=new Thickness(0,0,8,9) });
        controls.Children.Add(Section("3  Export"));
        var exports = new WrapPanel(); exports.Children.Add(export); exports.Children.Add(exportFiltered); controls.Children.Add(exports);
        controls.Children.Add(new TextBlock { Text = "All results exports every stored file. Filtered uses the type and disk selected on the right.", TextWrapping=TextWrapping.Wrap, Foreground=Brushes.DimGray, Margin=new Thickness(0,0,8,0) });
        Grid.SetRow(controls,2); source.Children.Add(controls); workspace.Children.Add(source);
        var splitter = new GridSplitter { Width=5, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Stretch, Background=Brushes.LightGray };
        Grid.SetColumn(splitter,1); workspace.Children.Add(splitter);

        var output = new DockPanel { Margin = new Thickness(8,0,0,0) };
        var tableTools = new WrapPanel();
        tableTools.Children.Add(Section("Results")); tableTools.Children.Add(filter);
        diskFilter.ItemsSource = new[] { new DiskChoice("All disks",null) }; diskFilter.SelectedIndex=0;
        tableTools.Children.Add(diskFilter);
        tableTools.Children.Add(Button("Previous",()=> { offset=Math.Max(0,offset-PageSize);RefreshPage(); }));
        tableTools.Children.Add(Button("Next",()=> { if(offset+PageSize<pageTotal) { offset+=PageSize;RefreshPage(); } }));
        pageLabel.Margin=new Thickness(8,8,0,0);tableTools.Children.Add(pageLabel);
        DockPanel.SetDock(tableTools,Dock.Top); output.Children.Add(tableTools);
        var copy = new WrapPanel { Margin=new Thickness(0,6,0,0) };
        copy.Children.Add(copyAlgorithm);copy.Children.Add(Button("Copy hash",CopyHash));copy.Children.Add(Button("Copy selected rows",CopyRows));
        DockPanel.SetDock(copy,Dock.Bottom);output.Children.Add(copy);
        var preview = new Grid { Height = 200 };
        preview.ColumnDefinitions.Add(new() { Width = new GridLength(330) });
        preview.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
        var visual = new DockPanel(); DockPanel.SetDock(previewLabel,Dock.Top);visual.Children.Add(previewLabel);
        visual.Children.Add(new ScrollViewer { Content=previews,VerticalScrollBarVisibility=ScrollBarVisibility.Auto });preview.Children.Add(visual);
        Grid.SetColumn(details,1);preview.Children.Add(details);
        var previewExpander=new Expander { Header="Selected file details & preview",Content=preview,IsExpanded=false };
        DockPanel.SetDock(previewExpander,Dock.Bottom); output.Children.Add(previewExpander);
        results.IsReadOnly=true;results.AutoGenerateColumns=false;results.SelectionMode=DataGridSelectionMode.Extended;results.SelectionUnit=DataGridSelectionUnit.FullRow;
        results.EnableRowVirtualization=true;results.EnableColumnVirtualization=true;results.CanUserAddRows=false;results.CanUserDeleteRows=false;results.CanUserSortColumns=false;
        results.HeadersVisibility=DataGridHeadersVisibility.Column;results.GridLinesVisibility=DataGridGridLinesVisibility.Horizontal;results.RowBackground=Brushes.White;
        results.AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(247,249,252));
        foreach(var col in new[] { ("File name","FileName",190d),("Type","Kind",65d),("Disk #","DiskNumber",65d),("Size (bytes)","SizeBytes",105d),("Status","ScanStatus",105d),("Hash","HashStatus",100d),("Metadata","MetadataStatus",105d),("Fingerprint","FingerprintStatus",105d) })
            results.Columns.Add(new DataGridTextColumn { Header=col.Item1,Binding=new Binding(col.Item2),Width=col.Item3 });
        foreach(var name in algorithms.Keys)results.Columns.Add(new DataGridTextColumn { Header=name,Binding=new Binding($"Hashes[{name}]"),Width=235 });
        results.Columns.Add(new DataGridTextColumn { Header="Disk serial",Binding=new Binding("DiskSerialNumber"),Width=170 });
        results.Columns.Add(new DataGridTextColumn { Header="Full path",Binding=new Binding("FullPath"),Width=300 });
        UpdateHashColumns();AutomationProperties.SetName(results,"Scan results");output.Children.Add(results);
        Grid.SetColumn(output,2);workspace.Children.Add(output);Grid.SetRow(workspace,2);outer.Children.Add(workspace);
        var footer=new StackPanel { Margin=new Thickness(18,0,18,12) };
        footer.Children.Add(scanLabel);footer.Children.Add(bar);footer.Children.Add(counts);footer.Children.Add(rateLabel);footer.Children.Add(activity);
        Grid.SetRow(footer,3);outer.Children.Add(footer);
        pause.Click+=(_,_)=>TogglePause();
        exportFiltered.Click+=async(_,_)=>await ExportAsync(true);
        diskFilter.SelectionChanged+=(_,_)=>{offset=0;RefreshPage();};
        return outer;
    }
    static TextBlock Section(string text)=>new() { Text=text,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,3,12,8) };
    void UpdateHashColumns()
    {
        foreach(var column in results.Columns)
            if(column.Header is string name && algorithms.TryGetValue(name,out var cb)) column.Visibility=cb.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
    }
    void TogglePause()
    {
        if(!busy||exporting||scanControl==null)return;
        if(scanControl.PauseRequested)scanControl.Resume();else scanControl.RequestPause();
        UpdatePauseState();
    }
    void UpdatePauseState()
    {
        pause.IsEnabled=busy&&!exporting&&scanCancel?.IsCancellationRequested==false;
        pause.Content=scanControl?.PauseRequested==true?"Continue":"Pause";
        if(scanControl?.IsPaused==true)activity.Text="Paused — current pass retained. Continue resumes it; Cancel saves partial results.";
        else if(scanControl?.PauseRequested==true)activity.Text="Pause requested — waiting for the current read or media operation to finish…";
    }
    async void RefreshPage()
    {
        if(pageLoading){pageQueued=true;return;}
        var active=store;if(active==null||active.Info.Status=="Resuming")return;
        pageLoading=true;var scope=CurrentFilter;int wanted=offset;
        try
        {
            var page=await Task.Run(()=>{long total=active.Count(scope);int at=wanted>=total?0:wanted;return(total,at,rows:active.GetPage(scope,at,PageSize),disks:active.GetDiskNumbers());});
            if(store!=active||wanted!=offset||scope.Kind!=CurrentFilter.Kind||scope.DiskNumber!=CurrentFilter.DiskNumber)return;
            var scroll = FindScrollViewer(results);
            bool preserveScroll = displayedScan == active.Info.ScanId && displayedKind == scope.Kind && displayedDisk == scope.DiskNumber && displayedOffset == page.at;
            double vertical = preserveScroll ? scroll?.VerticalOffset ?? 0 : 0;
            double horizontal = scroll?.HorizontalOffset ?? 0;
            var ids=results.SelectedItems.OfType<FileResult>().Select(x=>x.FileId).ToHashSet();
            refreshingRows=true;
            try { results.ItemsSource=page.rows;foreach(var row in page.rows)if(ids.Contains(row.FileId))results.SelectedItems.Add(row); }
            finally { refreshingRows=false; }
            if(busy || ids.Count>0) _=SelectAsync();
            offset=page.at;pageTotal=page.total;
            displayedScan=active.Info.ScanId;displayedKind=scope.Kind;displayedDisk=scope.DiskNumber;displayedOffset=page.at;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if(store==active && offset==page.at && CurrentFilter.Kind==scope.Kind && CurrentFilter.DiskNumber==scope.DiskNumber)
                { scroll?.ScrollToVerticalOffset(vertical);scroll?.ScrollToHorizontalOffset(horizontal); }
            }));
            pageLabel.Text=$"{(page.total==0?0:offset+1):N0}–{Math.Min(offset+PageSize,page.total):N0} of {page.total:N0}";
            var values=page.disks.Select(x=>new DiskChoice(x.Length==0?"Unknown disk":"Disk "+x,x)).Prepend(new DiskChoice("All disks",null)).ToList();
            if(!values.Select(x=>x.Number).SequenceEqual(diskFilter.Items.Cast<DiskChoice>().Select(x=>x.Number)))
            {
                var number=(diskFilter.SelectedItem as DiskChoice)?.Number;diskFilter.ItemsSource=values;diskFilter.SelectedItem=values.FirstOrDefault(x=>x.Number==number)??values[0];
            }
        }
        catch(ObjectDisposedException){}
        catch(Exception ex){if(store==active)activity.Text=ex.Message;}
        finally {pageLoading=false;if(pageQueued){pageQueued=false;RefreshPage();}}
    }
    static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if(parent is ScrollViewer viewer)return viewer;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(FindScrollViewer(VisualTreeHelper.GetChild(parent,i)) is { } found)return found;
        return null;
    }
    void UpdateRate()
    {
        var p=progress;if(p==null){rateLabel.Text="";return;}
        double now=elapsed.Elapsed.TotalSeconds, seconds=now-lastRateSeconds;
        if(seconds>=0.8)
        {
            long delta=Math.Max(0,p.BytesProcessed-lastRateBytes);currentRate=delta/seconds;lastRateBytes=p.BytesProcessed;lastRateSeconds=now;
        }
        string speed=scanControl?.IsPaused==true?"Paused":busy?$"Read rate {FormatBytes((long)currentRate)}/s":"";
        string file=p.CurrentFileBytes>0?$"Current file read {FormatBytes(p.CurrentFileBytes)}":"";
        rateLabel.Text=string.Join("  •  ",new[]{speed,file,"Read-only source access · SHA values cover the full file"}.Where(x=>x.Length>0));
    }
}

/// <summary>Folder navigation is lazy, off the UI thread and bounded per expansion.</summary>
internal sealed class FolderBrowser : TreeView
{
    readonly System.Collections.ObjectModel.ObservableCollection<string> roots;
    readonly Action<string> add;
    readonly List<(string path,CheckBox box)> boxes=[];
    bool syncing;
    public FolderBrowser(System.Collections.ObjectModel.ObservableCollection<string> selected,Action<string> addRoot)
    {
        roots=selected;add=addRoot;BorderThickness=new Thickness(0);
        AutomationProperties.SetName(this,"Folder browser");
        foreach(var drive in DriveInfo.GetDrives())Items.Add(Node(drive.Name,drive.Name));
        roots.CollectionChanged+=(_,_)=>{syncing=true;try{foreach(var (path,box) in boxes)box.IsChecked=roots.Contains(path,StringComparer.OrdinalIgnoreCase);}finally{syncing=false;}};
    }
    TreeViewItem Node(string path,string label)
    {
        var box=new CheckBox { Content=label,IsChecked=roots.Contains(path,StringComparer.OrdinalIgnoreCase),Margin=new Thickness(2,3,2,3),ToolTip=path };
        AutomationProperties.SetName(box,path);boxes.Add((path,box));
        box.Checked+=(_,_)=>{if(!syncing)add(path);};box.Unchecked+=(_,_)=>{if(!syncing){var selected=roots.FirstOrDefault(x=>x.Equals(path,StringComparison.OrdinalIgnoreCase));if(selected!=null)roots.Remove(selected);}};
        var node=new TreeViewItem { Header=box,Tag=path };node.Items.Add(new TreeViewItem { Header="Expand to load folders" });
        bool loaded=false;
        node.Expanded+=async(_,e)=>
        {
            if(e.OriginalSource!=node||loaded)return;loaded=true;node.Items.Clear();node.Items.Add(new TreeViewItem{Header="Loading…"});
            try
            {
                var children=await Task.Run(()=>Directory.EnumerateDirectories(path).Take(501).ToArray());node.Items.Clear();
                foreach(var child in children.Take(500))node.Items.Add(Node(child,Path.GetFileName(child)));
                if(children.Length>500)node.Items.Add(new TreeViewItem{Header="More folders: use Add folder",IsEnabled=false});
            }
            catch(Exception ex){node.Items.Clear();node.Items.Add(new TreeViewItem{Header="Unavailable — use Add folder",ToolTip=ex.Message,IsEnabled=false});}
        };
        return node;
    }
}
