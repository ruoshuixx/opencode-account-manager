using AccountManager.App.Services;
using AccountManager.Core.Actions;
using AccountManager.Core.OpenCode;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AccountManager.App.Views;

/// <summary>Editor for one API provider: name, protocol, baseURL, models and optional key rotation.</summary>
public partial class ApiConfigDialog : Window
{
    public ApiConfigDialog()
    {
        InitializeComponent();
    }

    public ApiConfigResult? Result { get; private set; }

    public static async Task<ApiConfigResult?> ShowAsync(Window owner, ApiConfigInput input)
    {
        var dialog = new ApiConfigDialog();
        dialog.HeaderText.Text = "配置供应商：" + input.Title;
        dialog.ProviderText.Text = "供应商 ID：" + input.ProviderId + "（保存时会写入全局配置）";
        dialog.NameBox.Text = string.IsNullOrWhiteSpace(input.DisplayName) ? input.Title : input.DisplayName;
        dialog.BaseUrlBox.Text = input.BaseUrl == "地址未配置" ? string.Empty : input.BaseUrl;
        dialog.ModelsBox.Text = string.Join(Environment.NewLine, input.Models);
        dialog.ResponsesRadio.IsChecked = input.UseResponses;
        dialog.ChatRadio.IsChecked = !input.UseResponses;
        dialog.KeyBox.IsEnabled = true;
        dialog.KeyBox.Watermark = input.HasCredential ? "留空保留当前密钥" : "该行没有本地密钥，可在此新建";
        dialog.HintText.Text = input.HasCredential
            ? "留空密钥将保留现有凭据；填写新密钥会先创建并验证新凭据，再替换旧凭据。"
            : "当前只有供应商配置，没有本地密钥；填写密钥会新建一份本地凭据。";
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            Result = new ApiConfigResult
            {
                DisplayName = ApiProviderValidation.Name(NameBox.Text),
                BaseUrl = ApiProviderValidation.Endpoint(BaseUrlBox.Text),
                Models = ApiProviderValidation.ParseModels(ModelsBox.Text),
                UseResponses = ResponsesRadio.IsChecked == true,
                NewKey = ApiProviderValidation.Key(KeyBox.Text, required: false),
            };
            KeyBox.Text = string.Empty;
            Close();
        }
        catch (OpenCodeException ex) { ErrorText.Text = ex.Message; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
