using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>人设页：进入即自动加载（#10）。</summary>
public sealed partial class PersonaPage : Page
{
    public PersonaPage()
    {
        Vm = App.Services.GetRequiredService<PersonaViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public PersonaViewModel Vm { get; }
}
