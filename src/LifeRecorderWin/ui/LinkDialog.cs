using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LifeRecorderWin.Ui;

/// <summary>
/// Google Cloud Console 에서 만든 "데스크톱 앱" OAuth 클라이언트를 입력받는다.
///
/// 안드로이드는 패키지명 + 서명 SHA-1 로 클라이언트를 등록해서 앱에 아무것도 넣지 않았지만,
/// 데스크톱 클라이언트는 ID 와 시크릿을 앱이 들고 있어야 한다.
/// (설치형 앱의 시크릿은 규격상 비밀이 아니다. 그래도 DPAPI 로 묶어 둔다)
/// </summary>
internal sealed class LinkDialog : Form
{
    private readonly TextBox _id = new();
    private readonly TextBox _secret = new();

    public string ClientId => _id.Text.Trim();
    public string ClientSecret => _secret.Text.Trim();

    public LinkDialog(string? currentId, string? currentSecret)
    {
        Text = "Google 계정 연결";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 250);
        Font = new Font("Segoe UI", 9f);

        var help = new Label
        {
            Text = "Google Cloud Console → API 및 서비스 → 사용자 인증 정보에서\n"
                   + "\"OAuth 클라이언트 ID\" 를 만들되 애플리케이션 유형을 **데스크톱 앱** 으로 고릅니다.\n"
                   + "Drive API 가 켜져 있어야 하고, 앱이 테스트 모드면 본인 계정을 테스트 사용자로 넣습니다.",
            Location = new Point(16, 14),
            Size = new Size(490, 62),
        };

        var link = new LinkLabel
        {
            Text = "Cloud Console 열기",
            Location = new Point(16, 78),
            AutoSize = true,
        };
        link.LinkClicked += (_, _) => OpenUrl("https://console.cloud.google.com/apis/credentials");

        var idLabel = new Label { Text = "클라이언트 ID", Location = new Point(16, 110), Size = new Size(100, 22) };
        _id.Location = new Point(120, 107);
        _id.Size = new Size(386, 24);
        _id.Text = currentId ?? "";

        var secretLabel = new Label { Text = "클라이언트 보안 비밀", Location = new Point(16, 144), Size = new Size(100, 34) };
        _secret.Location = new Point(120, 141);
        _secret.Size = new Size(386, 24);
        _secret.Text = currentSecret ?? "";
        _secret.UseSystemPasswordChar = true;

        var ok = new Button
        {
            Text = "연결",
            DialogResult = DialogResult.OK,
            Location = new Point(316, 196),
            Size = new Size(90, 30),
        };
        var cancel = new Button
        {
            Text = "취소",
            DialogResult = DialogResult.Cancel,
            Location = new Point(416, 196),
            Size = new Size(90, 30),
        };

        ok.Click += (_, _) =>
        {
            if (ClientId.Length == 0 || ClientSecret.Length == 0)
            {
                MessageBox.Show(this, "클라이언트 ID 와 보안 비밀을 모두 입력해 주세요.", "Life Recorder");
                DialogResult = DialogResult.None;
            }
        };

        Controls.AddRange(new Control[] { help, link, idLabel, _id, secretLabel, _secret, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    internal static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn("브라우저를 열지 못했습니다: " + e.Message);
        }
    }
}
