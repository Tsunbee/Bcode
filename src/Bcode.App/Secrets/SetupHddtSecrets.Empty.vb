' Bản RỖNG của SetupHddtSecrets.vb — được commit; SetupHDDT.vbproj chỉ link file này khi máy KHÔNG có SetupHddtSecrets.vb (file bí mật thật, gitignore).
' Tool vẫn build / mở được, bấm tạo script thì báo thiếu khoá. Thêm hằng: thêm vào CẢ HAI file. Xem README.md.
Friend Module SetupHddtSecrets
    ''' <summary>True = bản build có bí mật thật; False = bản rỗng.</summary>
    Public Const Present As Boolean = False

    ' Cặp khoá RSA của Proxy (ekey, dmstthddt.pk_service, API UpdateProxy) và của App (dmstthddt.pk / rk, edmkh.public_key) — trùng LocalSecrets.EInv* của Bcode.
    Public Const ProxyPublicKey As String = ""
    Public Const ProxyPrivateKey As String = ""
    Public Const AppPublicKey As String = ""
    Public Const AppPrivateKey As String = ""
    ' Hash password của edmkh / dmstthddt.
    Public Const ClientPassword As String = ""

    ' Tài khoản portal điền sẵn khi mở form (để thử).
    Public Const DefaultUser As String = ""
    Public Const DefaultPass As String = ""
End Module
