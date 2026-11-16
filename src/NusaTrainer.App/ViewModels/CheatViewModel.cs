using System.Globalization;
using NusaTrainer.App.Services;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

public sealed class CheatViewModel : ViewModelBase
{
    private TrainerSession? _session;
    private bool _isEnabled;
    private double _sliderValue;
    private string? _errorText;
    private bool _isBusy;
    private bool _suppress;

    // Penulisan slider di-coalesce: hanya nilai terakhir yang ditulis.
    private double? _pendingSlider;
    private bool _sliderWorker;

    public CheatDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string? Description => Definition.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Definition.Description);
    public bool ShowCategoryHeader { get; init; }

    public bool IsToggle => Definition.Type == CheatKind.Toggle;
    public bool IsSlider => Definition.Type == CheatKind.Slider;
    public bool IsButton => Definition.Type == CheatKind.Button;
    public bool IsNumber => Definition.Type == CheatKind.Number;
    public bool IsLockable => IsSlider && Definition.Operation == CheatOperation.Freeze;

    private string _numberText = "";
    private string? _currentValueText;

    /// <summary>Teks yang diketik pengguna untuk cheat Number.</summary>
    public string NumberText { get => _numberText; set => Set(ref _numberText, value); }

    /// <summary>Nilai di memori game saat ini (diperbarui berkala).</summary>
    public string? CurrentValueText
    {
        get => _currentValueText;
        private set { if (Set(ref _currentValueText, value)) OnPropertyChanged(nameof(HasCurrentValue)); }
    }
    public bool HasCurrentValue => _currentValueText is not null;

    public RelayCommand ApplyNumberCommand { get; private set; } = null!;

    public double Min => Definition.Min;
    public double Max => Definition.Max;
    public double Step => Definition.Step;

    public IReadOnlyList<string> Hotkeys { get; }
    public bool HasHotkeys => Hotkeys.Count > 0;

    public RelayCommand PressCommand { get; }

    public CheatViewModel(CheatDefinition definition)
    {
        Definition = definition;
        _sliderValue = definition.Default;
        var keys = new List<string>();
        if (definition.Hotkey is { } h) keys.Add(HotkeyParser.Pretty(h));
        if (definition.HotkeyDecrease is { } d) keys.Add("− " + HotkeyParser.Pretty(d));
        if (definition.HotkeyIncrease is { } i) keys.Add("+ " + HotkeyParser.Pretty(i));
        Hotkeys = keys;
        PressCommand = new RelayCommand(Press, () => CanInteract);
        ApplyNumberCommand = new RelayCommand(ApplyNumber, () => CanInteract);
    }

    private bool TryParseNumber(out double value) =>
        double.TryParse(_numberText.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(_numberText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void ApplyNumber()
    {
        var session = _session;
        if (session is null) return;
        if (!TryParseNumber(out var v)) { ErrorText = "Masukkan angka yang valid."; return; }
        IsBusy = true;
        ErrorText = null;
        Task.Run(() => session.SetNumber(Definition.Id, v)).ContinueWith(t => OnUi(() =>
        {
            IsBusy = false;
            if (t.Exception is { } ex) ErrorText = ex.InnerException?.Message ?? ex.Message;
        }));
    }

    /// <summary>Baca nilai terkini dari memori (dipanggil di thread latar).</summary>
    public void RefreshCurrentValue()
    {
        var session = _session;
        if (session is null || !IsNumber) return;
        string? text;
        try
        {
            var v = session.ReadValue(Definition.Id);
            text = Definition.ValueType is DataType.Float or DataType.Double
                ? v.ToString("0.###", CultureInfo.CurrentCulture)
                : v.ToString("N0", CultureInfo.CurrentCulture);
        }
        catch { text = "—"; }
        OnUi(() =>
        {
            CurrentValueText = text;
            if (_numberText.Length == 0 && text != "—") NumberText = text.Replace(CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator, "");
        });
    }

    public bool CanInteract => _session is not null && !_isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
                PressCommand.RaiseCanExecuteChanged();
                ApplyNumberCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value) || _suppress) return;
            _ = ApplyToggleAsync(value);
        }
    }

    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            value = Math.Round(Math.Clamp(value, Min, Max) / Step) * Step;
            if (!Set(ref _sliderValue, value)) return;
            OnPropertyChanged(nameof(SliderValueText));
            if (_session is not null) QueueSliderWrite(value);
        }
    }

    public string SliderValueText =>
        Step < 1 ? _sliderValue.ToString("0.0#", CultureInfo.CurrentCulture) : _sliderValue.ToString("N0", CultureInfo.CurrentCulture);

    public void Attach(TrainerSession? session)
    {
        _session = session;
        ErrorText = null;
        if (session is null) { SetEnabledSilently(false); CurrentValueText = null; }
        OnPropertyChanged(nameof(CanInteract));
        PressCommand.RaiseCanExecuteChanged();
        ApplyNumberCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Dipanggil bila sesi mematikan cheat sendiri (mis. pointer hilang).</summary>
    public void OnFailed(string message)
    {
        SetEnabledSilently(false);
        ErrorText = message;
    }

    public void ToggleFromHotkey()
    {
        if (!CanInteract) return;
        if (IsButton) Press();
        else if (IsToggle || IsLockable || IsNumber) IsEnabled = !IsEnabled;
    }

    public void StepFromHotkey(int direction)
    {
        if (!CanInteract) return;
        SliderValue += direction * Step;
        if (IsLockable && !IsEnabled) IsEnabled = true;
    }

    private void SetEnabledSilently(bool value)
    {
        _suppress = true;
        IsEnabled = value;
        _suppress = false;
    }

    private async Task ApplyToggleAsync(bool value)
    {
        var session = _session;
        if (session is null) { SetEnabledSilently(false); return; }
        IsBusy = true;
        ErrorText = null;
        try
        {
            bool hasNumber = TryParseNumber(out var number);
            await Task.Run(() =>
            {
                if (value && IsSlider) session.SetSliderValue(Definition.Id, _sliderValue);
                if (value && IsNumber)
                {
                    // Kunci angka yang diketik; bila kosong, kunci nilai saat ini.
                    session.SetNumber(Definition.Id, hasNumber ? number : session.ReadValue(Definition.Id));
                }
                session.SetToggle(Definition.Id, value);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Cheat {Definition.Id}: {ex.Message}");
            SetEnabledSilently(!value);
            ErrorText = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private void Press()
    {
        var session = _session;
        if (session is null) return;
        IsBusy = true;
        ErrorText = null;
        Task.Run(() => session.Press(Definition.Id)).ContinueWith(t => OnUi(() =>
        {
            IsBusy = false;
            if (t.Exception is { } ex) ErrorText = ex.InnerException?.Message ?? ex.Message;
        }));
    }

    private void QueueSliderWrite(double value)
    {
        _pendingSlider = value;
        if (_sliderWorker) return;
        _sliderWorker = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                double? v = null;
                await Dispatcher(() => { v = _pendingSlider; _pendingSlider = null; if (v is null) _sliderWorker = false; });
                if (v is null) return;
                try
                {
                    _session?.SetSliderValue(Definition.Id, v.Value);
                    OnUi(() => ErrorText = null);
                }
                catch (Exception ex) { OnUi(() => ErrorText = ex.Message); }
            }
        });
    }

    private static Task Dispatcher(Action a) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(a).GetTask();
}
