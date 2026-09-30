using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BcodeViewer.App.UI; // Nơi chứa ThemeManager

namespace BcodeViewer.App.Forms;

public sealed class QuickOpenForm : Form // Đã sửa thành Form chuẩn
{
    private readonly TextBox _searchBox;
    private readonly ListBox _resultBox;
    private readonly string _rootPath;
    private List<string> _allFiles = new();

    public string? SelectedFilePath { get; private set; }

    public QuickOpenForm(string rootPath)
    {
        _rootPath = rootPath;
        Text = "Mở nhanh File (Quick Open)";
        Width = 650;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        
        _searchBox = new TextBox { Dock = DockStyle.Top, Font = new Font("Segoe UI", 11f) };
        _searchBox.TextChanged += (_, _) => FilterFiles(_searchBox.Text);
        _searchBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Down && _resultBox.Items.Count > 0)
            {
                _resultBox.Focus();
                if (_resultBox.SelectedIndex < 0) _resultBox.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && _resultBox.SelectedItem is string path)
            {
                OpenFileAndClose(path);
                e.Handled = true;
            }
        };

        _resultBox = new ListBox 
        { 
            Dock = DockStyle.Fill, 
            IntegralHeight = false, 
            Font = new Font("Consolas", 10f),
            Margin = new Padding(0, 8, 0, 0)
        };
        _resultBox.DoubleClick += (_, _) =>
        {
            if (_resultBox.SelectedItem is string path) OpenFileAndClose(path);
        };
        _resultBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _resultBox.SelectedItem is string path)
            {
                OpenFileAndClose(path);
                e.Handled = true;
            }
        };

        panel.Controls.Add(_resultBox);
        panel.Controls.Add(_searchBox);
        Controls.Add(panel);

        // Áp dụng theme cho form này để đồng bộ giao diện
        ThemeManager.Apply(this);
        ThemeManager.ApplyTitleBar(this);

        Load += async (_, _) => await LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        if (string.IsNullOrWhiteSpace(_rootPath) || !Directory.Exists(_rootPath)) return;
        
        _searchBox.Enabled = false;
        _searchBox.Text = "Đang quét danh sách file trong App_Data...";

        await Task.Run(() =>
        {
            try
            {
                var extensions = new[] { "*.xml", "*.ent", "*.sql", "*.f", "*.txt", "*.js" };
                _allFiles = extensions
                    .SelectMany(ext => Directory.GetFiles(_rootPath, ext, SearchOption.AllDirectories))
                    .ToList();
            }
            catch
            {
                _allFiles = new List<string>();
            }
        });

        _searchBox.Enabled = true;
        _searchBox.Text = "";
        FilterFiles("");
        _searchBox.Focus();
    }

    private void FilterFiles(string keyword)
    {
        _resultBox.BeginUpdate();
        _resultBox.Items.Clear();

        var query = keyword.Trim().ToLowerInvariant();
        var matches = string.IsNullOrEmpty(query) 
            ? _allFiles.Take(100) 
            : _allFiles.Where(f => f.ToLowerInvariant().Contains(query)).Take(100);

        foreach (var file in matches)
        {
            var relative = file.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase) 
                ? file.Substring(_rootPath.Length).TrimStart('\\', '/') 
                : file;
            _resultBox.Items.Add(relative);
        }

        if (_resultBox.Items.Count > 0) _resultBox.SelectedIndex = 0;
        _resultBox.EndUpdate();
    }

    private void OpenFileAndClose(string relativePath)
    {
        var fullPath = Path.Combine(_rootPath, relativePath);
        if (File.Exists(fullPath))
        {
            SelectedFilePath = fullPath;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}