using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>模型配置页：一把全链路 key + 普通 LLM 与 JEV 两个固定块 + 搜索地址。</summary>
/// <remarks>
/// 密钥收敛后只剩一个 key 输入框。**框里的明文必须跟着 ViewModel 一起清掉** ——
/// 只清 VM 的话，点过保存后 PasswordBox 里还留着一串圆点：既让人以为没保存成功，
/// 也把明文多留在界面上一次（这正是「明文永不回显」要防的那一类）。
/// </remarks>
public sealed partial class EndpointsPage : Page
{
    public EndpointsPage()
    {
        Vm = App.Services.GetRequiredService<EndpointsViewModel>();
        InitializeComponent();

        // 页面与 VM 都是单例生命周期，这里不需要退订
        Vm.PropertyChanged += OnVmPropertyChanged;

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public EndpointsViewModel Vm { get; }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EndpointsViewModel.ApiKey))
        {
            return;
        }

        // 同值不写，避免「回填 → PasswordChanged → 再回填」的无谓循环
        if (!string.Equals(ApiKeyBox.Password, Vm.ApiKey, StringComparison.Ordinal))
        {
            ApiKeyBox.Password = Vm.ApiKey;
        }
    }

    /// <summary>把输入同步进 ViewModel；**保存完成后由 VM 清空，这里跟着一起清**。</summary>
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        => Vm.ApiKey = ((PasswordBox)sender).Password;
}
