#nullable enable
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Configurator;

public partial class ConnectionWindow : Window
{
    public Func<string, bool>? OnDatabaseAdded;
    public ConnectionSettings Settings { get; private set; } = new ConnectionSettings();

    private int _connectionGeneration;

    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(220, 70, 70));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(55, 160, 95));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(130, 140, 155));

    public ConnectionWindow()
    {
        InitializeComponent();
        var app = AppSettings.Load();
        TxtServer.Text = app.LastServer ?? string.Empty;
        TxtUser.Text = app.LastUser ?? string.Empty;
        AttachInvalidationHandlers();
    }

    private void AttachInvalidationHandlers()
    {
        TxtServer.TextChanged += (s, e) => InvalidateConnection();
        TxtUser.TextChanged += (s, e) => InvalidateConnection();
        PwdBox.PasswordChanged += (s, e) => InvalidateConnection();
    }

    private void InvalidateConnection()
    {
        _connectionGeneration++;
        CmbDatabase.ItemsSource = null;
        CmbDatabase.Items.Clear();
        CmbDatabase.SelectedIndex = -1;
        BtnAdd.IsEnabled = false;
        SetStatus("Готов к подключению", null);
    }

    private void SetStatus(string text, bool? success)
    {
        LblStatus.Text = text;
        LblStatus.Foreground = success == true ? SuccessBrush : success == false ? ErrorBrush : InfoBrush;
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        string server = TxtServer.Text.Trim();
        string user = TxtUser.Text.Trim();
        string password = PwdBox.Password;
        if (string.IsNullOrWhiteSpace(server)) { SetStatus("Укажите сервер.", false); return; }

        BtnConnect.IsEnabled = false;
        BtnAdd.IsEnabled = false;
        int generation = _connectionGeneration;
        try
        {
            var settings = new ConnectionSettings { Server = server, User = user, Password = password };
            using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(settings.ToConnectionString("master"));
            await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
            var databases = new List<string>();
            using (var command = new SqlCommand("SELECT name FROM sys.databases WHERE state = 0 AND database_id > 4 ORDER BY name", connection))
            using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) databases.Add(reader.GetString(0));

            if (generation != _connectionGeneration) return;
            CmbDatabase.ItemsSource = databases;
            if (databases.Count > 0) CmbDatabase.SelectedIndex = 0;
            BtnAdd.IsEnabled = databases.Count > 0;
            Settings = settings;
            SaveLastConnection(server, user);
            SetStatus($"Подключение успешно. Найдено баз: {databases.Count}", true);
        }
        catch (Exception ex)
        {
            if (generation == _connectionGeneration)
                SetStatus($"Не удалось подключиться. Проверьте данные входа и доверие к сертификату сервера: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", false);
        }
        finally { BtnConnect.IsEnabled = true; }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        string db = CmbDatabase.SelectedItem?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(db)) return;
        Settings = new ConnectionSettings { Server = TxtServer.Text.Trim(), User = TxtUser.Text.Trim(), Password = PwdBox.Password };
        SaveLastConnection(Settings.Server, Settings.User);
        bool added = OnDatabaseAdded?.Invoke(db) ?? false;
        SetStatus(added ? $"База «{db}» добавлена." : $"База «{db}» уже подключена.", added);
    }

    private static void SaveLastConnection(string server, string user)
    {
        var settings = AppSettings.Load();
        settings.LastServer = server;
        settings.LastUser = user;
        AppSettings.Save(settings);
    }
}
