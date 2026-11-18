using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using YukMain.Host.Core.Controllers;
using YukMain.Host.HidMaestro;

namespace YukMain.Host.Desktop;

public partial class MainWindow : Window
{
    private readonly ControllerManager _manager = new(new HidMaestroBackend());
    private readonly Dictionary<int, ControllerState> _drafts = new()
    {
        [2] = default, [3] = default, [4] = default
    };

    private readonly ToggleButton[] _buttonControls;
    private int _player = 2;
    private bool _updatingUi;
    private bool _initialized;
    private bool _testPending;

    public MainWindow()
    {
        InitializeComponent();
        _buttonControls =
        [
            ButtonA, ButtonB, ButtonX, ButtonY,
            ButtonLB, ButtonRB, ButtonStart, ButtonBack
        ];
        TestPanel.Visibility = Visibility.Visible;
        RefreshSlot();
    }

    private void Initialize_Click(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        try
        {
            DriverStatusText.Text = "Initializing Windows driver...";
            InitializeButton.IsEnabled = false;
            _manager.Initialize();
            _initialized = true;
            DriverStatusText.Text = "Driver initialized / already current";
            StatusText.Text = "Driver ready. Create a virtual controller to begin testing.";
        }
        catch (Exception ex)
        {
            DriverStatusText.Text = "Driver initialization failed";
            ShowError("Driver initialization failed", ex);
            InitializeButton.IsEnabled = true;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_initialized)
            {
                StatusText.Text = "Initialize the driver first.";
                return;
            }

            if (_manager.IsConnected(_player)) return;
            _manager.Create(_player);
            _manager.Submit(_player, _drafts[_player]);
            StatusText.Text = $"Player {_player} virtual Xbox controller created.";
            RefreshSlot();
        }
        catch (Exception ex) { ShowError("Controller creation failed", ex); }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_testPending) return;
            if (_manager.IsConnected(_player))
                _manager.Remove(_player);
            _drafts[_player] = default;
            LoadInputs(default);
            RefreshSlot();
            StatusText.Text = $"Player {_player} controller removed.";
        }
        catch (Exception ex) { ShowError("Controller removal failed", ex); }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_testPending) return;
        try
        {
            _drafts[_player] = default;
            LoadInputs(default);
            if (_manager.IsConnected(_player))
                _manager.Reset(_player);
            StatusText.Text = $"Player {_player}: all inputs released.";
        }
        catch (Exception ex) { ShowError("Reset failed", ex); }
    }

    private void PlayerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PlayerTabs) || _testPending) return;
        _player = PlayerTabs.SelectedIndex + 2;
        if (_player is < 2 or > 4) return;
        LoadInputs(_drafts[_player]);
        RefreshSlot();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _testPending || !IsLoaded || _buttonControls is null) return;
        try
        {
            var state = ReadInputs();
            _drafts[_player] = state;
            if (_manager.IsConnected(_player))
                _manager.Submit(_player, state);
        }
        catch (Exception ex) { ShowError("Input submission failed", ex); }
    }

    private ControllerState ReadInputs()
    {
        ControllerButtons buttons = ControllerButtons.None;
        foreach (var button in _buttonControls)
        {
            if (button.IsChecked == true && Enum.TryParse<ControllerButtons>(button.Tag?.ToString(), out var value))
                buttons |= value;
        }

        var direction = DPadDirection.None;
        if (DPadPicker.SelectedItem is ComboBoxItem choice)
            Enum.TryParse(choice.Tag?.ToString(), out direction);

        return new ControllerState
        {
            Buttons = buttons,
            DPad = direction,
            LeftX = (float)LeftX.Value,
            LeftY = (float)LeftY.Value,
            RightX = (float)RightX.Value,
            RightY = (float)RightY.Value,
            LeftTrigger = (float)LeftTrigger.Value,
            RightTrigger = (float)RightTrigger.Value
        };
    }

    private void LoadInputs(ControllerState state)
    {
        _updatingUi = true;
        try
        {
            foreach (var button in _buttonControls)
            {
                if (Enum.TryParse<ControllerButtons>(button.Tag?.ToString(), out var flag))
                    button.IsChecked = (state.Buttons & flag) == flag;
            }
            foreach (ComboBoxItem item in DPadPicker.Items)
            {
                if (item.Tag?.ToString() == state.DPad.ToString())
                {
                    DPadPicker.SelectedItem = item;
                    break;
                }
            }
            LeftX.Value = state.LeftX;
            LeftY.Value = state.LeftY;
            RightX.Value = state.RightX;
            RightY.Value = state.RightY;
            LeftTrigger.Value = state.LeftTrigger;
            RightTrigger.Value = state.RightTrigger;
        }
        finally { _updatingUi = false; }
    }

    private void RefreshSlot()
    {
        if (!IsLoaded && !IsInitialized) return;
        SlotLabel.Text = $"Player {_player} controller";
        SlotStatus.Text = _manager.IsConnected(_player)
            ? "Connected — use the controls below to submit input"
            : "Disconnected — click Create Controller";
    }

    private async void DelayedTest_Click(object sender, RoutedEventArgs e)
    {
        if (_testPending) return;
        try
        {
            if (!_manager.IsConnected(_player))
            {
                StatusText.Text = "Create this player's controller first.";
                return;
            }

            _testPending = true;
            var selected = _player;
            PlayerTabs.IsEnabled = false;
            // Neutralize held buttons first, so a scripted tap is unambiguous.
            _manager.Reset(selected);
            for (var seconds = 5; seconds > 0; seconds--)
            {
                StatusText.Text = $"Player {selected}: pressing A in {seconds}s — switch to your game now.";
                await Task.Delay(1000);
            }

            StatusText.Text = $"Player {selected}: A PRESSED";
            _manager.Submit(selected, new ControllerState { Buttons = ControllerButtons.A });
            await Task.Delay(350);
            _manager.Reset(selected);
            StatusText.Text = $"Player {selected}: A released. Script finished.";
            // Restore the UI draft, but not the previous held output; the test exits neutral.
            _drafts[selected] = default;
            LoadInputs(default);
        }
        catch (Exception ex) { ShowError("Scripted test failed", ex); }
        finally
        {
            // Always try to neutralize after interrupted tests.
            try { if (_manager.IsConnected(_player)) _manager.Reset(_player); }
            catch { /* Best effort after errors. */ }
            PlayerTabs.IsEnabled = true;
            _testPending = false;
        }
    }

    private void JoyCpl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "joy.cpl") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Unable to open Windows controller tester", ex); }
    }

    private void ShowError(string title, Exception error)
    {
        StatusText.Text = $"{title}: {error.Message}";
        MessageBox.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosed(EventArgs e)
    {
        _manager.Dispose();
        base.OnClosed(e);
    }
}
