using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Bannerlord.NativeCharacterImageGenerator.App;

public partial class MainWindow : Window
{
    private IReadOnlyList<NativeCharacterDefinition> _characters = [];
    private IReadOnlyList<NativeCharacterDefinition> _filteredCharacters = [];
    private CharacterRenderService? _renderer;
    private NativeEngineRenderService? _nativeEngineRenderer;
    private BitmapSource? _currentBitmap;
    private ReignCatalogResult? _reignCatalog;
    private CanonicalCharacterCatalogResult? _canonicalCatalog;
    private string _gameRoot = string.Empty;
    private string _lastAiCacheRoot = string.Empty;
    private readonly Dictionary<string, string> _generatedSources = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _nativeRenderCancellation;
    private CancellationTokenSource? _batchRenderCancellation;
    private AppearanceProviderSettings _appearanceProviderSettings;
    private AppearanceIntentExtractionService _appearanceExtraction;
    private AppearanceAnalysisResult? _appearanceAnalysis;
    private AppearanceCandidateView? _activeAppearanceCandidate;
    private string _referenceImagePath = string.Empty;
    private int _appearanceReroll;
    private int _renderVersion;
    private bool _loaded;
    private bool _batchRunning;

    public MainWindow()
    {
        _appearanceProviderSettings = AppearanceProviderSettings.FromEnvironment();
        _appearanceExtraction = new AppearanceIntentExtractionService(
            new RuleBasedAppearanceIntentAnalyzer(),
            _appearanceProviderSettings.IsConfigured
                ? new OpenAiCompatibleAppearanceIntentAnalyzer(_appearanceProviderSettings)
                : null);
        InitializeComponent();
        Loaded += MainWindow_OnLoaded;
        Closing += (_, _) =>
        {
            _nativeRenderCancellation?.Cancel();
            _batchRenderCancellation?.Cancel();
        };
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            IProgress<string> progress = new Progress<string>(message => StatusText.Text = message);
            var initialized = await Task.Run(() =>
            {
                var gameRoot = GameInstallation.ResolveRoot(null);
                progress.Report("Reading native characters, Reign rosters, and civilian equipment…");
                var catalog = CharacterCatalog.Load(gameRoot);
                var canonicalCatalog = CanonicalCharacterCatalog.Load(gameRoot, catalog.Characters);
                var reignCatalog = ReignCharacterCatalog.Merge(gameRoot, catalog.Characters);
                var assets = NativeAssetRepository.Load(gameRoot, progress);
                return (
                    GameRoot: gameRoot,
                    Catalog: catalog,
                    CanonicalCatalog: canonicalCatalog,
                    ReignCatalog: reignCatalog,
                    Assets: assets);
            });

            _reignCatalog = initialized.ReignCatalog;
            _canonicalCatalog = initialized.CanonicalCatalog;
            _gameRoot = initialized.GameRoot;
            _characters = initialized.ReignCatalog.Characters;
            _renderer = new CharacterRenderService(initialized.Catalog, initialized.Assets);
            _nativeEngineRenderer = new NativeEngineRenderService(initialized.GameRoot);
            _loaded = true;
            ApplyFilter();
            BuildAllButton.IsEnabled = _characters.Count > 0;
            BuildAiCacheButton.IsEnabled = _canonicalCatalog.Characters.Count > 0;
            ResumeAiCacheButton.IsEnabled = true;
            AppearanceProviderEndpointBox.Text = _appearanceProviderSettings.Endpoint;
            AppearanceProviderModelBox.Text = _appearanceProviderSettings.Model;
            CreateCandidatesButton.IsEnabled = _characters.Any(character =>
                AppearanceCandidateGenerator.IsValidBodyKey(character.BodyKey));
            AppearanceAnalysisStatusText.Text = _appearanceProviderSettings.IsConfigured
                ? $"Optional text/vision provider ready: {_appearanceProviderSettings.Model}. API credentials are never displayed or logged."
                : "Built-in description analysis is ready. Image-only creation requires NATIVE_CHARACTER_AI_API_KEY and NATIVE_CHARACTER_AI_MODEL.";
            StatusText.Text =
                $"Ready — {_reignCatalog.NativePortraitCount:N0} exact Reign captures and " +
                $"{_reignCatalog.CampaignCharacterCount:N0} saved campaign characters; " +
                $"{initialized.Assets.MetameshCount:N0} native models indexed. Ready for fresh native rendering.";
            EmptyPreviewText.Text = "Select a character to render.";

            var preferred = _characters.FirstOrDefault(character => character.Id == "main_hero")
                ?? _characters.FirstOrDefault();
            if (preferred is not null)
            {
                CharacterList.SelectedItem = preferred;
                CharacterList.ScrollIntoView(preferred);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not initialize: {exception.Message}";
            EmptyPreviewText.Text = "The native catalog could not be loaded.";
            MessageBox.Show(
                this,
                exception.Message,
                "Bannerlord Native Character Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        _filteredCharacters = string.IsNullOrWhiteSpace(query)
            ? _characters
            : _characters.Where(character =>
                    character.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || character.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || character.CharacterObjectId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || character.Culture.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || character.RosterSource.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        CharacterList.ItemsSource = _filteredCharacters;
        var nativeCount = _characters.Count(character =>
            character.SourceModule.Equals("Native", StringComparison.OrdinalIgnoreCase));
        var warSailsCount = _characters.Count(character =>
            character.SourceModule.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase));
        var reignCount = _characters.Count(character =>
            character.SourceModule.Equals("ReignBeta", StringComparison.OrdinalIgnoreCase));
        CharacterCountText.Text =
            $"{_filteredCharacters.Count:N0} of {_characters.Count:N0} characters  |  " +
            $"Native {nativeCount:N0}  |  War Sails {warSailsCount:N0}  |  Reign {reignCount:N0}";
        BuildFilteredButton.IsEnabled = !_batchRunning
            && !string.IsNullOrWhiteSpace(query)
            && _filteredCharacters.Count > 0;
    }

    private async void CharacterList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _renderer is null || CharacterList.SelectedItem is not NativeCharacterDefinition character)
        {
            return;
        }

        _activeAppearanceCandidate = null;
        await RenderSelectedCharacter(character);
    }

    private void ChooseReferenceImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a portrait or reference image",
            Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _referenceImagePath = dialog.FileName;
        ReferenceImageText.Text = Path.GetFileName(dialog.FileName);
        ReferenceImageText.ToolTip = dialog.FileName;
    }

    private void ClearReferenceImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        _referenceImagePath = string.Empty;
        ReferenceImageText.Text = "No image selected";
        ReferenceImageText.ToolTip = null;
    }

    private void ApplyAppearanceProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var enteredKey = AppearanceProviderApiKeyBox.Password;
        _appearanceProviderSettings = new AppearanceProviderSettings(
            AppearanceProviderEndpointBox.Text.Trim(),
            AppearanceProviderModelBox.Text.Trim(),
            string.IsNullOrWhiteSpace(enteredKey) ? _appearanceProviderSettings.ApiKey : enteredKey);
        _appearanceExtraction = new AppearanceIntentExtractionService(
            new RuleBasedAppearanceIntentAnalyzer(),
            _appearanceProviderSettings.IsConfigured
                ? new OpenAiCompatibleAppearanceIntentAnalyzer(_appearanceProviderSettings)
                : null);
        AppearanceProviderApiKeyBox.Clear();
        AppearanceAnalysisStatusText.Text = _appearanceProviderSettings.IsConfigured
            ? $"Helper LLM ready for text and vision analysis with model {_appearanceProviderSettings.Model}. The API key is held in memory and will not be logged."
            : "Helper LLM is not fully configured. Description-only creation will use the built-in analyzer.";
    }

    private async void CreateCandidatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        _appearanceAnalysis = null;
        _appearanceReroll = 0;
        await CreateAppearanceCandidatesAsync(extractIntent: true);
    }

    private async void RerollCandidatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_appearanceAnalysis is null)
        {
            return;
        }
        _appearanceReroll++;
        await CreateAppearanceCandidatesAsync(extractIntent: false);
    }

    private async Task CreateAppearanceCandidatesAsync(bool extractIntent)
    {
        if (!_loaded || _nativeEngineRenderer is null || _batchRunning)
        {
            return;
        }

        int? requestedSeed = null;
        if (!string.IsNullOrWhiteSpace(AppearanceSeedBox.Text))
        {
            if (!int.TryParse(AppearanceSeedBox.Text.Trim(), out var parsedSeed) || parsedSeed < 0)
            {
                AppearanceAnalysisStatusText.Text = "Seed must be a non-negative whole number or blank.";
                return;
            }
            requestedSeed = parsedSeed;
        }

        _nativeRenderCancellation?.Cancel();
        _batchRenderCancellation?.Cancel();
        _batchRenderCancellation?.Dispose();
        _batchRenderCancellation = new CancellationTokenSource();
        var cancellationToken = _batchRenderCancellation.Token;
        CreateCandidatesButton.IsEnabled = false;
        RerollCandidatesButton.IsEnabled = false;
        ExportAppearanceButton.IsEnabled = false;
        AppearanceProgressBar.Visibility = Visibility.Visible;
        AppearanceCandidateList.ItemsSource = null;
        SetBatchRunning(true, 4);
        try
        {
            if (extractIntent)
            {
                AppearanceAnalysisStatusText.Text = "Extracting a bounded semantic appearance intent...";
                _appearanceAnalysis = await _appearanceExtraction.ExtractAsync(
                    new AppearanceAnalysisRequest(AppearanceDescriptionBox.Text, _referenceImagePath),
                    cancellationToken);
            }

            var analysis = _appearanceAnalysis
                ?? throw new InvalidOperationException("No extracted appearance intent is available.");
            AppearanceAnalysisStatusText.Text =
                $"{analysis.Analyzer} extracted the intent. {analysis.Notice}".Trim();
            var candidates = AppearanceCandidateGenerator.Generate(
                analysis.Intent,
                _characters,
                requestedSeed,
                count: 4,
                reroll: _appearanceReroll);
            var progress = new Progress<NativeEngineBatchProgress>(update =>
            {
                AppearanceAnalysisStatusText.Text =
                    $"Rendering native candidate {Math.Min(update.Completed + update.Failed + 1, update.Total)} of {update.Total}: {update.Message}";
            });
            var renderResult = await _nativeEngineRenderer.RenderBatchAsync(
                candidates.Select(candidate => candidate.Character).ToArray(),
                768,
                960,
                progress,
                cancellationToken);

            var views = new List<AppearanceCandidateView>();
            foreach (var candidate in candidates)
            {
                var rendered = renderResult.Items.FirstOrDefault(item =>
                    item.Character.Id.Equals(candidate.Character.Id, StringComparison.OrdinalIgnoreCase));
                BitmapSource? preview = null;
                if (rendered is { Success: true } && File.Exists(rendered.RawOutputPath))
                {
                    preview = CreateNativePortraitBitmap(
                        await File.ReadAllBytesAsync(rendered.RawOutputPath, cancellationToken),
                        1d,
                        upperBodySource: true);
                }
                views.Add(new AppearanceCandidateView(candidate, preview, rendered?.Error ?? string.Empty));
            }

            AppearanceCandidateList.ItemsSource = views;
            if (views.Count > 0)
            {
                AppearanceCandidateList.SelectedIndex = 0;
            }
            var renderedCount = views.Count(view => view.Preview is not null);
            AppearanceAnalysisStatusText.Text =
                $"Created {views.Count} deterministic candidates; {renderedCount} native previews rendered. " +
                (string.IsNullOrWhiteSpace(analysis.Notice) ? string.Empty : analysis.Notice);
            RerollCandidatesButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            AppearanceAnalysisStatusText.Text = "Appearance candidate creation was canceled.";
        }
        catch (Exception exception)
        {
            AppearanceAnalysisStatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                "Appearance Lab",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            AppearanceProgressBar.Visibility = Visibility.Collapsed;
            SetBatchRunning(false, 4);
            CreateCandidatesButton.IsEnabled = _loaded;
            RerollCandidatesButton.IsEnabled = _appearanceAnalysis is not null;
        }
    }

    private void AppearanceCandidateList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppearanceCandidateList.SelectedItem is not AppearanceCandidateView view)
        {
            return;
        }

        _activeAppearanceCandidate = view;
        _currentBitmap = view.Preview;
        PreviewImage.Source = view.Preview;
        EmptyPreviewText.Visibility = view.Preview is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreviewText.Text = view.Preview is null
            ? "This candidate did not render. Reroll or inspect the extraction status."
            : string.Empty;
        SelectedNameText.Text = view.Title;
        SelectedDetailsText.Text = view.Details;
        SaveButton.IsEnabled = view.Preview is not null;
        ExportAppearanceButton.IsEnabled = true;
        StatusText.Text =
            "Appearance Lab candidates are render-only. Selection and export do not create or modify a campaign hero.";
        ShowEquipment(view.Candidate.Character, headgearSuppressed: true);
    }

    private void ExportAppearanceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeAppearanceCandidate is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export selected native appearance",
            Filter = "Bannerlord appearance JSON (*.json)|*.json",
            FileName = $"appearance-candidate-{_activeAppearanceCandidate.Candidate.Index + 1}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var export = AppearanceCharacterExport.FromCandidate(_activeAppearanceCandidate.Candidate);
        File.WriteAllText(dialog.FileName, AppearanceCharacterExport.Serialize(export));
        StatusText.Text = $"Exported stable native appearance contract: {dialog.FileName}";
    }

    private async Task RenderSelectedCharacter(NativeCharacterDefinition character)
    {
        if (_renderer is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _renderVersion);
        SelectedNameText.Text = character.Name;
        SelectedDetailsText.Text =
            $"{character.DisplayCulture} • {(character.IsFemale ? "Female" : "Male")} • " +
            $"Age {character.Age:0.#} • {character.Id} • {character.RosterSource}";
        _currentBitmap = null;
        var exactRequested = CachedCaptureMode.IsChecked == true;
        var freshRequested = NativeEngineMode.IsChecked == true;
        GenerateNativeButton.IsEnabled = !_batchRunning
            && freshRequested
            && _nativeEngineRenderer is not null;
        if (freshRequested)
        {
            YawSlider.IsEnabled = false;
            if (_generatedSources.TryGetValue(CharacterKey(character), out var generatedPath)
                && File.Exists(generatedPath))
            {
                await ShowGeneratedSource(character, generatedPath, version);
            }
            else
            {
                PreviewImage.Source = null;
                EmptyPreviewText.Visibility = Visibility.Visible;
                EmptyPreviewText.Text = "Click Generate New Native Source to create a fresh frame.";
                SaveButton.IsEnabled = false;
                StatusText.Text =
                    "This mode uses Bannerlord's native CharacterTableau renderer in a render-only worker. " +
                    "It does not read or copy the cached source image.";
                ShowEquipment(character, headgearSuppressed: true);
            }
            return;
        }

        if (exactRequested && character.HasNativePortrait)
        {
            await ShowNativeCapture(character, version);
            return;
        }

        StatusText.Text = "Loading native civilian meshes and rendering offline preview…";
        EquipmentText.Text = string.Empty;
        EmptyPreviewText.Visibility = Visibility.Visible;
        EmptyPreviewText.Text = "Rendering native assets…";
        SaveButton.IsEnabled = false;

        try
        {
            var renderer = _renderer;
            var yaw = (float)YawSlider.Value;
            var zoom = (float)ZoomSlider.Value;
            var output = await Task.Run(() => renderer.Render(character, 760, 760, yaw, zoom));
            if (version != _renderVersion)
            {
                return;
            }

            _currentBitmap = CreateBitmap(output.Image);
            PreviewImage.Source = _currentBitmap;
            EmptyPreviewText.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            YawSlider.IsEnabled = true;
            StatusText.Text =
                (exactRequested
                    ? "No Reign in-game source capture exists for this character yet. "
                    : string.Empty) +
                $"Rendered {output.Image.TriangleCount:N0} native triangles in the rotatable offline preview." +
                (output.MissingAssets.Count > 0
                    ? $" Missing: {string.Join(", ", output.MissingAssets)}"
                    : string.Empty);
            EquipmentText.Text = output.ResolvedItems.Count == 0
                ? $"Civilian template: {character.CivilianTemplate}"
                : $"Civilian clothing: {string.Join("  •  ", output.ResolvedItems)}";
        }
        catch (Exception exception)
        {
            if (version != _renderVersion)
            {
                return;
            }

            StatusText.Text = $"Render failed: {exception.Message}";
            EmptyPreviewText.Text = "Unable to render this character.";
        }
    }

    private async Task ShowNativeCapture(NativeCharacterDefinition character, int version)
    {
        StatusText.Text = "Loading Reign's native in-game capture…";
        EquipmentText.Text = string.Empty;
        EmptyPreviewText.Visibility = Visibility.Visible;
        EmptyPreviewText.Text = "Loading exact Bannerlord capture…";
        SaveButton.IsEnabled = false;
        YawSlider.IsEnabled = false;

        try
        {
            var bytes = await File.ReadAllBytesAsync(character.NativePortraitPath);
            if (version != _renderVersion)
            {
                return;
            }

            _currentBitmap = CreateNativePortraitBitmap(bytes, ZoomSlider.Value, upperBodySource: false);
            PreviewImage.Source = _currentBitmap;
            EmptyPreviewText.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            StatusText.Text =
                "Exact native Bannerlord image loaded from Reign's local portrait cache. " +
                "The game and Reign server are not running; turn is disabled for this captured view.";
            var clothing = character.CivilianEquipment
                .Where(item => item.Slot is "Head" or "Body" or "Cape" or "Gloves" or "Leg")
                .ToArray();
            EquipmentText.Text = clothing.Length == 0
                ? "Civilian equipment is embedded in the native capture."
                : "Civilian clothing: " + string.Join(
                    "  •  ",
                    clothing.Select(item => $"{item.Slot}: {item.ItemId}"));
        }
        catch (Exception exception)
        {
            if (version != _renderVersion)
            {
                return;
            }

            StatusText.Text = $"Native capture failed: {exception.Message}";
            EmptyPreviewText.Text = "Unable to load the Reign capture.";
        }
    }

    private async Task ShowGeneratedSource(
        NativeCharacterDefinition character,
        string path,
        int version)
    {
        EmptyPreviewText.Visibility = Visibility.Visible;
        EmptyPreviewText.Text = "Loading newly generated native source…";
        SaveButton.IsEnabled = false;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (version != _renderVersion)
            {
                return;
            }
            _currentBitmap = CreateNativePortraitBitmap(bytes, ZoomSlider.Value, upperBodySource: true);
            PreviewImage.Source = _currentBitmap;
            EmptyPreviewText.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            StatusText.Text =
                $"Fresh native source generated from 3D body and civilian-equipment data. File: {path}";
            ShowEquipment(character, headgearSuppressed: true);
        }
        catch (Exception exception)
        {
            if (version != _renderVersion)
            {
                return;
            }
            StatusText.Text = $"Could not load the newly generated source: {exception.Message}";
            EmptyPreviewText.Text = "Unable to load the new native source.";
        }
    }

    private async void GenerateNativeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_batchRunning
            || _nativeEngineRenderer is null
            || CharacterList.SelectedItem is not NativeCharacterDefinition character)
        {
            return;
        }

        _nativeRenderCancellation?.Cancel();
        _nativeRenderCancellation?.Dispose();
        _nativeRenderCancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _renderVersion);
        GenerateNativeButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        EmptyPreviewText.Visibility = Visibility.Visible;
        EmptyPreviewText.Text = "Starting native renderer…";
        var progress = new Progress<string>(message =>
        {
            StatusText.Text = message;
            EmptyPreviewText.Text = message;
        });
        try
        {
            var result = await _nativeEngineRenderer.RenderAsync(
                character,
                1024,
                1280,
                progress,
                _nativeRenderCancellation.Token);
            if (version != _renderVersion)
            {
                return;
            }
            _generatedSources[CharacterKey(character)] = result.OutputPath;
            _currentBitmap = CreateNativePortraitBitmap(result.PngBytes, ZoomSlider.Value, upperBodySource: true);
            PreviewImage.Source = _currentBitmap;
            EmptyPreviewText.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            StatusText.Text =
                "Generated a new source frame with Bannerlord's native 3D renderer. " +
                $"No cached portrait PNG was used. File: {result.OutputPath}";
            ShowEquipment(character, headgearSuppressed: true);
        }
        catch (OperationCanceledException)
        {
            if (version == _renderVersion)
            {
                StatusText.Text = "Native source generation was canceled.";
                EmptyPreviewText.Text = "Generation canceled.";
            }
        }
        catch (Exception exception)
        {
            if (version == _renderVersion)
            {
                StatusText.Text = $"Native source generation failed: {exception.Message}";
                EmptyPreviewText.Text = "The native renderer did not produce an image.";
            }
        }
        finally
        {
            if (version == _renderVersion)
            {
                GenerateNativeButton.IsEnabled = !_batchRunning;
            }
        }
    }

    private async void BuildAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StartSourceBuildAsync(_characters, "all");
    }

    private async void BuildFilteredButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StartSourceBuildAsync(_filteredCharacters, "search");
    }

    private async void BuildAiCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_canonicalCatalog is null || _nativeEngineRenderer is null || _batchRunning)
        {
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose where the Reign shared portrait source-cache package will be created",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Multiselect = false
        };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        var packageRoot = AiSourceCacheBuildCoordinator.CreatePackageRoot(folderDialog.FolderName);
        await StartAiCacheBuildAsync(packageRoot, SelectedAiCacheProfile());
    }

    private async void ResumeAiCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_canonicalCatalog is null || _nativeEngineRenderer is null || _batchRunning)
        {
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose an incomplete Reign shared portrait source-cache package",
            InitialDirectory = Directory.Exists(_lastAiCacheRoot)
                ? _lastAiCacheRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Multiselect = false
        };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        var packageRoot = folderDialog.FolderName;
        if (Path.GetFileName(packageRoot).Equals("_shared", StringComparison.OrdinalIgnoreCase))
        {
            packageRoot = Directory.GetParent(packageRoot)?.FullName ?? packageRoot;
        }
        if (!File.Exists(Path.Combine(packageRoot, "build_manifest.json")))
        {
            MessageBox.Show(
                this,
                "The selected folder is not an existing source-cache package. Select the folder containing build_manifest.json.",
                "Resume Source Cache Build",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AiSourceCacheRenderProfile packageProfile;
        try
        {
            packageProfile = ReadPackageRenderProfile(packageRoot);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Resume Source Cache Build",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        SelectAiCacheProfile(packageProfile.RenderContractVersion);
        await StartAiCacheBuildAsync(packageRoot, packageProfile);
    }

    private void OpenAiCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_lastAiCacheRoot))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(_lastAiCacheRoot);
        Process.Start(startInfo);
    }

    private async Task StartAiCacheBuildAsync(
        string packageRoot,
        AiSourceCacheRenderProfile renderProfile)
    {
        if (_batchRunning || _canonicalCatalog is null || _nativeEngineRenderer is null)
        {
            return;
        }

        _nativeRenderCancellation?.Cancel();
        _batchRenderCancellation?.Cancel();
        _batchRenderCancellation?.Dispose();
        _batchRenderCancellation = new CancellationTokenSource();
        var cancellationToken = _batchRenderCancellation.Token;
        _lastAiCacheRoot = Path.GetFullPath(packageRoot);
        OpenAiCacheButton.IsEnabled = false;
        var eligibleCount = _canonicalCatalog.Characters.Count(AiSourceCacheBuildCoordinator.IsAiPortraitEligible);
        var excludedUnderage = _canonicalCatalog.Characters.Count - eligibleCount;
        SetBatchRunning(true, eligibleCount);

        var progress = new Progress<AiSourceCacheProgress>(update =>
        {
            BatchProgressBar.Maximum = Math.Max(1, update.Total);
            BatchProgressBar.Value = Math.Clamp(update.Validated + update.Failed, 0, update.Total);
            BatchProgressText.Text =
                $"Rendered {update.Rendered:N0}  |  Validated {update.Validated:N0}  |  " +
                $"Skipped {update.Skipped:N0}  |  Failed {update.Failed:N0}" +
                (string.IsNullOrWhiteSpace(update.Current) ? string.Empty : $"\nCurrent: {update.Current}");
            StatusText.Text = update.Message;
        });

        try
        {
            StatusText.Text =
                $"Preparing {eligibleCount:N0} adult AI sources; {excludedUnderage:N0} underage characters are excluded.";
            var coordinator = new AiSourceCacheBuildCoordinator(
                _nativeEngineRenderer,
                _canonicalCatalog,
                installedSharedRoot: Path.Combine(
                    _gameRoot,
                    "Modules",
                    "ReignBeta",
                    "PortraitCache",
                    "_shared"),
                renderProfile: renderProfile);
            var result = await coordinator.RunAsync(_lastAiCacheRoot, progress, cancellationToken);
            OpenAiCacheButton.IsEnabled = Directory.Exists(result.PackageRoot);

            if (result.Complete)
            {
                StatusText.Text =
                    $"AI source cache complete: {result.Validated:N0} validated adults; " +
                    $"{excludedUnderage:N0} underage characters excluded.";
                MessageBox.Show(
                    this,
                    $"The complete {result.Validated:N0}-adult AI source cache is ready.\n" +
                    $"{excludedUnderage:N0} underage characters were excluded.\n\n{result.PackageRoot}",
                    "AI Source Cache Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                StatusText.Text = result.Canceled
                    ? "AI source-cache build canceled. Validated entries were preserved and can be resumed."
                    : $"AI source-cache build incomplete: {result.Validated:N0} validated, {result.Failed:N0} failed.";
                MessageBox.Show(
                    this,
                    $"Validated work was preserved. Use Resume Source Cache Build to continue.\n\n" +
                    $"Validated: {result.Validated:N0}\nSkipped: {result.Skipped:N0}\nFailed: {result.Failed:N0}\n\n" +
                    result.PackageRoot,
                    "AI Source Cache Incomplete",
                    MessageBoxButton.OK,
                    result.Failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            OpenAiCacheButton.IsEnabled = Directory.Exists(_lastAiCacheRoot);
            StatusText.Text = $"AI source-cache build stopped: {exception.Message}";
            MessageBox.Show(
                this,
                exception.Message + "\n\nValidated files and the manifest were preserved for resume.",
                "AI Source Cache Build",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBatchRunning(false, _canonicalCatalog.Characters.Count);
            OpenAiCacheButton.IsEnabled = Directory.Exists(_lastAiCacheRoot);
        }
    }

    private AiSourceCacheRenderProfile SelectedAiCacheProfile()
    {
        var version = (AiCachePresetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return AiSourceCacheContract.GetProfile(version);
    }

    private static AiSourceCacheRenderProfile ReadPackageRenderProfile(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "build_manifest.json");
        var manifest = JsonSerializer.Deserialize<SourceCacheBuildManifestV2>(
            File.ReadAllText(manifestPath),
            AiSourceCacheContract.JsonOptions)
            ?? throw new InvalidDataException("The source-cache manifest could not be read.");
        return AiSourceCacheContract.GetProfile(manifest.RenderContractVersion);
    }

    private void SelectAiCacheProfile(string contractVersion)
    {
        foreach (var item in AiCachePresetBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString()?.Equals(contractVersion, StringComparison.OrdinalIgnoreCase) == true)
            {
                AiCachePresetBox.SelectedItem = item;
                return;
            }
        }
    }

    private void CancelBatchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _batchRenderCancellation?.Cancel();
        BatchProgressText.Text = "Canceling the source build and closing its engine worker...";
    }

    private async Task StartSourceBuildAsync(
        IReadOnlyList<NativeCharacterDefinition> characters,
        string buildKind)
    {
        if (_batchRunning || _nativeEngineRenderer is null || characters.Count == 0)
        {
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose where the Bannerlord character source build will be created",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Multiselect = false
        };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        var label = buildKind == "all" ? "ALL" : "Search Results";
        var outputRoot = Path.Combine(
            folderDialog.FolderName,
            $"Bannerlord Character Source Build - {DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(outputRoot);

        _nativeRenderCancellation?.Cancel();
        _batchRenderCancellation?.Cancel();
        _batchRenderCancellation?.Dispose();
        _batchRenderCancellation = new CancellationTokenSource();
        var cancellationToken = _batchRenderCancellation.Token;
        SetBatchRunning(true, characters.Count);

        var progress = new Progress<NativeEngineBatchProgress>(update =>
        {
            var processed = Math.Clamp(update.Completed + update.Failed, 0, update.Total);
            BatchProgressBar.Maximum = Math.Max(1, update.Total);
            BatchProgressBar.Value = processed;
            BatchProgressText.Text =
                $"Native rendering {processed:N0} of {update.Total:N0}" +
                (update.Failed > 0 ? $"  |  {update.Failed:N0} failed" : string.Empty) +
                (string.IsNullOrWhiteSpace(update.CurrentCharacterId)
                    ? string.Empty
                    : $"\nCurrent: {update.CurrentCharacterId}");
            StatusText.Text = string.IsNullOrWhiteSpace(update.Message)
                ? $"Building {label} character source images in one persistent engine session..."
                : update.Message;
        });

        try
        {
            var result = await _nativeEngineRenderer.RenderBatchAsync(
                characters,
                1024,
                1280,
                progress,
                cancellationToken);

            var published = new List<object>(result.Items.Count);
            var saved = 0;
            var publishFailures = 0;
            foreach (var item in result.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var characterFolder = Path.Combine(
                    outputRoot,
                    $"{SanitizeFileName(item.Character.Name)} ({SanitizeFileName(item.Character.Id)})");
                var sourcePath = Path.Combine(characterFolder, "source.png");
                var error = item.Error;
                if (item.Success)
                {
                    try
                    {
                        await Task.Run(
                            () => SaveBatchSource(item.RawOutputPath, sourcePath),
                            cancellationToken);
                        saved++;
                    }
                    catch (Exception exception)
                    {
                        error = exception.Message;
                        publishFailures++;
                    }
                }

                published.Add(new
                {
                    id = item.Character.Id,
                    name = item.Character.Name,
                    sourceModule = item.Character.SourceModule,
                    rosterSource = item.Character.RosterSource,
                    outputPath = File.Exists(sourcePath) ? sourcePath : string.Empty,
                    success = File.Exists(sourcePath),
                    error
                });
                var processed = published.Count;
                BatchProgressBar.Maximum = Math.Max(1, result.Items.Count);
                BatchProgressBar.Value = processed;
                BatchProgressText.Text = $"Framing and saving {processed:N0} of {result.Items.Count:N0}";
            }

            var manifestPath = Path.Combine(outputRoot, "manifest.json");
            var moduleCounts = characters
                .GroupBy(character => character.SourceModule, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        version = 1,
                        createdUtc = DateTime.UtcNow,
                        buildKind,
                        requested = characters.Count,
                        rendered = result.Completed,
                        nativeRenderFailures = result.Failed,
                        saved,
                        publishFailures,
                        sourceZoom = 4.0,
                        headgearSuppressed = true,
                        persistentEngineSession = true,
                        moduleCounts,
                        items = published
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            BatchProgressText.Text =
                $"Complete: {saved:N0} of {characters.Count:N0} saved" +
                (result.Failed + publishFailures > 0
                    ? $"  |  {result.Failed + publishFailures:N0} failed (see manifest)"
                    : string.Empty);
            StatusText.Text =
                $"Source build complete. The engine stayed loaded for the batch. Output: {outputRoot}";
            SetBatchRunning(false, characters.Count);
            MessageBox.Show(
                this,
                $"Saved {saved:N0} source images.\n\n{outputRoot}\n\n" +
                "manifest.json records every requested character and any failures.",
                "Bannerlord Character Source Build",
                MessageBoxButton.OK,
                result.Failed + publishFailures == 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                "Source build canceled. The persistent engine worker was closed; completed raw frames remain in the local app data cache.";
            BatchProgressText.Text = "Build canceled.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Source build failed: {exception.Message}";
            BatchProgressText.Text = "Build stopped. See the status message for details.";
            MessageBox.Show(
                this,
                exception.Message,
                "Bannerlord Character Source Build",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBatchRunning(false, characters.Count);
        }
    }

    private void SetBatchRunning(bool running, int total)
    {
        _batchRunning = running;
        BuildAllButton.IsEnabled = !running && _loaded && _characters.Count > 0;
        BuildFilteredButton.IsEnabled = !running
            && _loaded
            && !string.IsNullOrWhiteSpace(SearchBox.Text)
            && _filteredCharacters.Count > 0;
        BuildAiCacheButton.IsEnabled = !running
            && _loaded
            && _canonicalCatalog is not null
            && _canonicalCatalog.Characters.Count > 0;
        ResumeAiCacheButton.IsEnabled = !running && _loaded && _canonicalCatalog is not null;
        AiCachePresetBox.IsEnabled = !running;
        OpenAiCacheButton.IsEnabled = !running && Directory.Exists(_lastAiCacheRoot);
        GenerateNativeButton.IsEnabled = !running
            && NativeEngineMode.IsChecked == true
            && CharacterList.SelectedItem is NativeCharacterDefinition;
        CreateCandidatesButton.IsEnabled = !running && _loaded;
        RerollCandidatesButton.IsEnabled = !running && _appearanceAnalysis is not null;
        CancelBatchButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BatchProgressBar.Visibility = Visibility.Visible;
        BatchProgressText.Visibility = Visibility.Visible;
        if (running)
        {
            BatchProgressBar.Minimum = 0;
            BatchProgressBar.Maximum = Math.Max(1, total);
            BatchProgressBar.Value = 0;
            BatchProgressText.Text = $"Preparing {total:N0} source images...";
        }
    }

    private static void SaveBatchSource(string rawPath, string sourcePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var bytes = File.ReadAllBytes(rawPath);
        var source = CreateNativePortraitBitmap(bytes, 4.0, upperBodySource: true);
        using var stream = File.Create(sourcePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
    }

    private async void YawSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded
            || CachedCaptureMode.IsChecked == true
            || NativeEngineMode.IsChecked == true
            || CharacterList.SelectedItem is not NativeCharacterDefinition character)
        {
            return;
        }

        await Task.Delay(120);
        if (Math.Abs(YawSlider.Value - e.NewValue) < 0.01)
        {
            await RenderSelectedCharacter(character);
        }
    }

    private async void ZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || CharacterList.SelectedItem is not NativeCharacterDefinition character)
        {
            return;
        }

        await Task.Delay(120);
        if (Math.Abs(ZoomSlider.Value - e.NewValue) < 0.001)
        {
            await RenderSelectedCharacter(character);
        }
    }

    private async void RenderMode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || CharacterList.SelectedItem is not NativeCharacterDefinition character)
        {
            return;
        }

        await RenderSelectedCharacter(character);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var character = _activeAppearanceCandidate?.Candidate.Character
            ?? CharacterList.SelectedItem as NativeCharacterDefinition;
        if (_currentBitmap is null || character is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save native character image",
            Filter = "PNG image (*.png)|*.png",
            FileName = SanitizeFileName(character.Name) + ".png",
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        using var stream = File.Create(dialog.FileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_currentBitmap));
        encoder.Save(stream);
        StatusText.Text = $"Saved {dialog.FileName}";
    }

    private sealed record AppearanceCandidateView(
        AppearanceCandidate Candidate,
        BitmapSource? Preview,
        string Error)
    {
        public string Title => Candidate.Character.Name;
        public string Details =>
            $"{Candidate.Character.DisplayCulture} | {(Candidate.Character.IsFemale ? "Female" : "Male")} | " +
            $"Age {Candidate.Character.Age:0.#} | Seed {Candidate.Seed}" +
            (string.IsNullOrWhiteSpace(Error) ? string.Empty : $" | Render failed: {Error}");
    }

    private static BitmapSource CreateNativePortraitBitmap(byte[] bytes, double zoom, bool upperBodySource)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        if (zoom <= 1.01)
        {
            return bitmap;
        }

        var zoomRange = upperBodySource ? 3d : 3.63d;
        var amount = Math.Clamp((zoom - 1d) / zoomRange, 0d, 1d);
        var cropScale = Math.Max(0.28d, 1d - (0.85d * amount));
        var cropHeight = Math.Clamp((int)Math.Round(bitmap.PixelHeight * cropScale), 1, bitmap.PixelHeight);
        var cropWidth = Math.Clamp(
            (int)Math.Round(cropHeight * 1.05d),
            1,
            bitmap.PixelWidth);
        var centerX = bitmap.PixelWidth / 2d;
        var centerYRatio = upperBodySource
            // Full source zoom is deliberately framed higher so tall hair and the top
            // of the head remain inside the output while keeping the shoulders visible.
            ? 0.42d - (0.07d * amount)
            : 0.55d - (0.05d * amount);
        var centerY = bitmap.PixelHeight * centerYRatio;
        var x = Math.Clamp((int)Math.Round(centerX - (cropWidth / 2d)), 0, bitmap.PixelWidth - cropWidth);
        var y = Math.Clamp((int)Math.Round(centerY - (cropHeight / 2d)), 0, bitmap.PixelHeight - cropHeight);
        var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, cropWidth, cropHeight));
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource CreateBitmap(RenderResult result)
    {
        var bgra = new byte[result.Rgba.Length];
        for (var index = 0; index < result.Rgba.Length; index += 4)
        {
            bgra[index] = result.Rgba[index + 2];
            bgra[index + 1] = result.Rgba[index + 1];
            bgra[index + 2] = result.Rgba[index];
            bgra[index + 3] = result.Rgba[index + 3];
        }

        var source = BitmapSource.Create(
            result.Width,
            result.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            result.Width * 4);
        source.Freeze();
        return source;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static string CharacterKey(NativeCharacterDefinition character) =>
        character.ReignCampaignId + "|" + character.Id + "|" + character.CharacterObjectId;

    private void ShowEquipment(NativeCharacterDefinition character, bool headgearSuppressed = false)
    {
        var clothing = character.CivilianEquipment
            .Where(item => item.Slot is "Body" or "Cape" or "Gloves" or "Leg"
                || (!headgearSuppressed && item.Slot == "Head"))
            .ToArray();
        var description = clothing.Length == 0
            ? $"Civilian template: {character.CivilianTemplate}"
            : "Civilian clothing: " + string.Join(
                "  •  ",
                clothing.Select(item => $"{item.Slot}: {item.ItemId}"));
        EquipmentText.Text = headgearSuppressed
            ? "Headgear: suppressed for source capture  •  " + description
            : description;
    }
}
