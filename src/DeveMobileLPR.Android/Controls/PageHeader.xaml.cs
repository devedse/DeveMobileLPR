namespace DeveMobileLPR.AndroidApp.Controls;

public partial class PageHeader : ContentView
{
    public static readonly BindableProperty EyebrowProperty = BindableProperty.Create(
        nameof(Eyebrow), typeof(string), typeof(PageHeader), string.Empty);

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(PageHeader), string.Empty);

    public static readonly BindableProperty IsBusyProperty = BindableProperty.Create(
        nameof(IsBusy), typeof(bool), typeof(PageHeader), false);

    public PageHeader() => InitializeComponent();

    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }
}
