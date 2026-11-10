using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using NowBar.App.Helpers;
using NowBar.App.Services;

namespace NowBar.App.Views;

/// <summary>업데이트 직후 한 번 보여 주는 "새로 바뀐 점" 팝업.</summary>
public partial class WhatsNewWindow : Window
{
    private WhatsNewWindow(Version current, IReadOnlyList<ReleaseNoteSection> sections)
    {
        InitializeComponent();
        HeaderText.Text = $"NowBar v{current.ToString(3)} 업데이트 완료";
        foreach (var section in sections) AddSection(section, showVersion: sections.Count > 1);
    }

    /// <summary>NowBar 캡슐 바로 아래 가운데에 띄운다.</summary>
    public static WhatsNewWindow Show(Window nowBar, double belowY, Version current, IReadOnlyList<ReleaseNoteSection> sections)
    {
        var window = new WhatsNewWindow(current, sections);
        void Place()
        {
            window.Left = nowBar.Left + (nowBar.ActualWidth - window.ActualWidth) / 2;
            window.Top = nowBar.Top + belowY;
        }
        window.Left = nowBar.Left + (nowBar.ActualWidth - window.Width) / 2;
        window.Top = nowBar.Top + belowY;
        window.Loaded += (_, _) => Place();
        window.Show();
        return window;
    }

    private Style Res(string key) => (Style)FindResource(key);

    private void AddSection(ReleaseNoteSection section, bool showVersion)
    {
        if (showVersion)
        {
            NotesPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(section.Date) ? $"v{section.Version}" : $"v{section.Version} · {section.Date}",
                Style = Res("Text.Strong"),
                FontSize = 13,
                Foreground = (Brush)FindResource("Brush.Accent"),
                Margin = new Thickness(0, NotesPanel.Children.Count == 0 ? 0 : 14, 0, 2),
            });
        }

        foreach (var line in section.Lines)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("### "))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = line[4..].Trim(),
                    Style = Res("Text.Sub"),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 8, 0, 4),
                });
                continue;
            }

            var trimmed = line.TrimStart();
            var bullet = trimmed.StartsWith("- ");
            var text = bullet ? trimmed[2..] : line;
            var body = new TextBlock
            {
                Style = Res("Text.Base"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                LineHeight = 19,
                VerticalAlignment = VerticalAlignment.Top,
            };
            foreach (var inline in FormatInline(text)) body.Inlines.Add(inline);

            if (!bullet)
            {
                body.Margin = new Thickness(0, 2, 0, 2);
                NotesPanel.Children.Add(body);
                continue;
            }

            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var dot = new TextBlock
            {
                Text = "•",
                Style = Res("Text.Base"),
                Foreground = (Brush)FindResource("Brush.Accent"),
                VerticalAlignment = VerticalAlignment.Top,
                LineHeight = 19,
                Margin = new Thickness(2, 0, 8, 0),
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(body);
            NotesPanel.Children.Add(row);
        }
    }

    private static readonly Regex InlineToken = new(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);

    /// <summary>**굵게**, `코드`, [링크](주소) 정도만 처리한 짧은 마크다운.</summary>
    public static IEnumerable<Inline> FormatInline(string text)
    {
        var last = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > last) yield return new Run(text[last..m.Index]);
            if (m.Groups[1].Success) yield return new Bold(new Run(m.Groups[1].Value));
            else if (m.Groups[2].Success) yield return new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Consolas") };
            else yield return new Run(m.Groups[3].Value);
            last = m.Index + m.Length;
        }
        if (last < text.Length) yield return new Run(text[last..]);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Logger.Error("릴리스 페이지 열기 실패", ex); }
        Close();
    }
}
