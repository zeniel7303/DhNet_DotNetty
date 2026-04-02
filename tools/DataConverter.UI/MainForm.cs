using DataConverter.Converters;
using System.Text;

namespace DataConverter.UI;

public class MainForm : Form
{
    private TextBox     _excelDirBox  = null!;
    private TextBox     _outputDirBox = null!;
    private Button      _convertBtn   = null!;
    private RichTextBox _logBox       = null!;

    public MainForm()
    {
        BuildLayout();
        var root = FindSolutionRoot();
        _excelDirBox.Text  = Path.Combine(root, "Bin", "excel");
        _outputDirBox.Text = Path.Combine(root, "Bin", "resources");
    }

    // ── UI 구성 ──────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        Text            = "DataConverter";
        Size            = new Size(640, 480);
        MinimumSize     = new Size(520, 380);
        StartPosition   = FormStartPosition.CenterScreen;
        Font            = new Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            Padding     = new Padding(12),
            RowCount    = 4,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        root.Controls.Add(MakePathRow("Excel 폴더", out _excelDirBox, OnBrowseExcel),  0, 0);
        root.Controls.Add(MakePathRow("출력 폴더",  out _outputDirBox, OnBrowseOutput), 0, 1);
        root.Controls.Add(MakeButtonRow(), 0, 2);

        _logBox = new RichTextBox
        {
            Dock      = DockStyle.Fill,
            ReadOnly  = true,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.FromArgb(180, 220, 180),
            Font      = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        root.Controls.Add(_logBox, 0, 3);
    }

    private static Panel MakePathRow(string label, out TextBox textBox, EventHandler browse)
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        var lbl = new Label
        {
            Text     = label,
            Width    = 72,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock     = DockStyle.Left,
        };

        var btn = new Button
        {
            Text   = "찾기",
            Width  = 56,
            Dock   = DockStyle.Right,
        };
        btn.Click += browse;

        textBox = new TextBox { Dock = DockStyle.Fill };

        // 추가 순서가 Dock 레이아웃에 영향을 주므로 Right/Left 먼저
        panel.Controls.Add(textBox);
        panel.Controls.Add(btn);
        panel.Controls.Add(lbl);
        return panel;
    }

    private Panel MakeButtonRow()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };

        _convertBtn = new Button { Text = "변환 실행", Width = 110, Height = 30, Left = 0, Top = 0 };
        _convertBtn.Click += OnConvert;

        panel.Controls.Add(_convertBtn);
        return panel;
    }

    // ── 이벤트 핸들러 ────────────────────────────────────────────────────────

    private void OnBrowseExcel(object? sender, EventArgs e)  => BrowseFolder(_excelDirBox);
    private void OnBrowseOutput(object? sender, EventArgs e) => BrowseFolder(_outputDirBox);

    private void BrowseFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = target.Text };
        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private async void OnConvert(object? sender, EventArgs e)
    {
        await RunAsync(() =>
        {
            Console.WriteLine($"[DataConverter] Excel  : {Path.GetFullPath(_excelDirBox.Text)}");
            Console.WriteLine($"[DataConverter] Output : {Path.GetFullPath(_outputDirBox.Text)}");
            TableConverter.RunAll(_excelDirBox.Text, _outputDirBox.Text);
            Console.WriteLine("[DataConverter] 완료.");
        });
    }

    private async Task RunAsync(Action action)
    {
        _convertBtn.Enabled = false;
        _logBox.Clear();

        var prevOut = Console.Out;
        Console.SetOut(new RichTextBoxWriter(_logBox));
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            Log($"[오류] {ex.Message}");
        }
        finally
        {
            Console.SetOut(prevOut);
            _convertBtn.Enabled = true;
        }
    }

    private void Log(string message)
    {
        if (_logBox.InvokeRequired) _logBox.Invoke(() => Log(message));
        else _logBox.AppendText(message + Environment.NewLine);
    }

    // ── 유틸 ─────────────────────────────────────────────────────────────────

    /// <summary>AppContext.BaseDirectory에서 위로 올라가며 .sln 파일이 있는 폴더를 반환한다.</summary>
    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return AppContext.BaseDirectory;
    }
}

/// <summary>Console.SetOut 리디렉션용 — TableConverter의 Console.WriteLine을 RichTextBox에 출력한다.</summary>
file sealed class RichTextBoxWriter : TextWriter
{
    private readonly RichTextBox _rtb;
    public RichTextBoxWriter(RichTextBox rtb) => _rtb = rtb;
    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value) => Append((value ?? "") + Environment.NewLine);
    public override void Write(string? value)     => Append(value ?? "");

    private void Append(string text)
    {
        if (_rtb.InvokeRequired) _rtb.Invoke(() => Append(text));
        else
        {
            _rtb.AppendText(text);
            _rtb.ScrollToCaret();
        }
    }
}
