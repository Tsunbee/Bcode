namespace Bcode.Chat.Net;

/// <summary>Tự kiểm phần mạng trên 1 máy (Bhoo.exe --selftest): 2 nút chỉ nghe 127.0.0.1, cổng riêng, dữ liệu ở thư mục tạm.
/// Kiểm: tìm thấy nhau, chat 1-1 + ack, BUZZ, chat nhóm, gửi file, tin offline (tắt B → A gửi → mở lại B → tự nhận). Không đụng dữ liệu thật.</summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(TextWriter log)
    {
        var root = Path.Combine(Path.GetTempPath(), "Bhoo.SelfTest." + Environment.ProcessId);
        Directory.CreateDirectory(root);
        var dirA = Path.Combine(root, "A");
        var dirB = Path.Combine(root, "B");
        const int discA = 48960, discB = 48961, portA = 48970, portB = 48980;
        var fails = 0;
        void Check(bool ok, string what) { log.WriteLine((ok ? "  OK   " : "  LỖI  ") + what); if (!ok) fails++; }

        static async Task<bool> Until(Func<bool> cond, int ms = 8000)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }

        ChatNode MakeB(List<(string Conv, ChatMessage Msg)> inbox)
        {
            var sb = new ChatStore(dirB);
            sb.Prefs.Id = "binh"; sb.Prefs.Name = "Binh"; sb.Prefs.ManualPeers = new() { "127.0.0.1:" + discA };
            var nb = new ChatNode(sb, discB, portB, loopbackOnly: true);
            nb.MessageReceived += (c, m) => { lock (inbox) inbox.Add((c, m)); };
            nb.Start();
            return nb;
        }

        var sa = new ChatStore(dirA);
        sa.Prefs.Id = "an.nguyen"; sa.Prefs.Name = "An.Nguyen"; sa.Prefs.ManualPeers = new() { "127.0.0.1:" + discB };
        var a = new ChatNode(sa, discA, portA, loopbackOnly: true);
        var inA = new List<(string Conv, ChatMessage Msg)>();
        var delivered = new List<string>();
        var served = new List<string>();
        a.MessageReceived += (c, m) => { lock (inA) inA.Add((c, m)); };
        a.Delivered += (_, mid, _) => { lock (delivered) delivered.Add(mid); };
        a.FileServed += (fid, _) => { lock (served) served.Add(fid); };
        a.Start();
        var inB = new List<(string Conv, ChatMessage Msg)>();
        var b = MakeB(inB);
        var idA = sa.Prefs.Id;
        var idB = b.MyId;
        try
        {
            log.WriteLine("Bhoo — tự kiểm (127.0.0.1)");
            Check(await Until(() => a.FindBuddy(idB)?.Online == true && b.FindBuddy(idA)?.Online == true), "Hai máy thấy nhau online");

            var m1 = a.Send("u:" + idB, "text", "Xin chào 👋 tiếng Việt có dấu", style: "b", fg: "#cc0000", size: 14);
            Check(await Until(() => { lock (inB) return inB.Any(x => x.Msg.Mid == m1.Mid && x.Conv == "u:" + idA); }), "B nhận tin 1-1 (đúng cuộc chat)");
            Check(await Until(() => { lock (delivered) return delivered.Contains(m1.Mid); }), "A nhận ack, outbox trống");
            Check(inB.First(x => x.Msg.Mid == m1.Mid).Msg is { Text: "Xin chào 👋 tiếng Việt có dấu", Style: "b", Fg: "#cc0000" }, "Nội dung + kiểu chữ giữ nguyên");

            var bz = b.Send("u:" + idA, "buzz", "BUZZ!!!");
            Check(await Until(() => { lock (inA) return inA.Any(x => x.Msg.Mid == bz.Mid && x.Msg.Kind == "buzz"); }), "A nhận BUZZ");

            var g = a.CreateGroup("Phòng thử", new[] { idB });
            Check(await Until(() => b.FindGroup(g.Gid) is { Members.Count: 2 }), "B thấy phòng nhóm do A tạo");
            var gm = b.Send("g:" + g.Gid, "text", "chào cả phòng");
            Check(await Until(() => { lock (inA) return inA.Any(x => x.Msg.Mid == gm.Mid && x.Conv == "g:" + g.Gid); }), "A nhận tin nhóm của B");

            var src = Path.Combine(root, "thu file.bin");
            var data = new byte[3_500_000];
            new Random(7).NextBytes(data);
            await File.WriteAllBytesAsync(src, data);
            var fm = a.OfferFile("u:" + idB, src);
            Check(await Until(() => { lock (inB) return inB.Any(x => x.Msg.Mid == fm.Mid); }), "B nhận tin gửi file");
            var got = await b.DownloadAsync(inB.First(x => x.Msg.Mid == fm.Mid).Msg, Path.Combine(root, "nhan"));
            Check(got is not null && (await File.ReadAllBytesAsync(got)).AsSpan().SequenceEqual(data), "B tải file đủ + đúng nội dung (3,5 MB)");
            Check(await Until(() => { lock (served) return served.Contains(fm.File!.Fid); }), "A biết B đã tải xong");
            var bad = new ChatMessage { From = idA, FromName = "An", Kind = "file", File = new FileMeta { Fid = fm.File!.Fid, Name = "x", Size = 1, Token = "sai" } };
            Check(await b.DownloadAsync(bad, Path.Combine(root, "nhan")) is null, "Sai mã file thì bị từ chối");

            // ảnh đại diện: A đặt ảnh → B tự tải về; A bỏ ảnh → B xoá
            var jpg = new byte[3000]; new Random(7).NextBytes(jpg); jpg[0] = 0xFF; jpg[1] = 0xD8;
            a.SetAvatar(jpg);
            Check(await Until(() => b.FindBuddy(idA)?.AvHash == a.Prefs.AvatarHash && File.Exists(b.AvatarPath(idA))
                && File.ReadAllBytes(b.AvatarPath(idA)).AsSpan().SequenceEqual(jpg)), "B tự tải ảnh đại diện mới của A");
            var notImg = false; try { a.SetAvatar(new byte[200]); } catch (ArgumentException) { notImg = true; }
            Check(notImg, "Không nhận file không phải ảnh làm ảnh đại diện");
            a.SetAvatar(null);
            Check(await Until(() => b.FindBuddy(idA)?.AvHash == "" && !File.Exists(b.AvatarPath(idA))), "A bỏ ảnh → B xoá ảnh cũ");

            // trùng nick: máy C đăng nhập bằng đúng ID của A → cả hai đều biết
            var sc = new ChatStore(Path.Combine(root, "C"));
            sc.Prefs.Id = idA; sc.Prefs.Name = "an.nguyen"; sc.Prefs.ManualPeers = new() { "127.0.0.1:" + discA };
            var c = new ChatNode(sc, 48962, 48990, loopbackOnly: true);
            string? cSaw = null, aSaw = null;
            c.NickConflict += ip => cSaw ??= ip;
            a.NickConflict += ip => aSaw ??= ip;
            c.Start();
            c.Probe();
            Check(await Until(() => cSaw is not null && aSaw is not null, 3000), "Trùng Bhoo ID: máy mới và máy đang dùng đều phát hiện");
            c.Dispose();
            Check(Nick.Valid("ken_2000") && Nick.Valid("Ba.Ken") && !Nick.Valid("ab") && !Nick.Valid("1ken") && !Nick.Valid("kên"), "Kiểm tra định dạng Bhoo ID");

            b.SetMe(status: Status.Invisible);
            Check(await Until(() => a.FindBuddy(idB)?.Online == false), "B chuyển Ẩn → A thấy B offline");
            b.SetMe(status: Status.Online);
            Check(await Until(() => a.FindBuddy(idB)?.Online == true, 6000), "B hiện lại → A thấy B online");

            b.Dispose();
            Check(await Until(() => a.FindBuddy(idB)?.Online == false), "A thấy B offline khi B thoát");
            var off = a.Send("u:" + idB, "text", "tin gửi lúc B offline");
            Check(a.Pending(off.Mid).Contains(idB), "Tin nằm chờ trong outbox");
            var sa2 = new ChatStore(dirA);
            Check(sa2.Outbox.Any(o => o.Msg.Mid == off.Mid), "Outbox đã ghi ra đĩa");

            var inB2 = new List<(string Conv, ChatMessage Msg)>();
            b = MakeB(inB2);
            Check(await Until(() => { lock (inB2) return inB2.Any(x => x.Msg.Mid == off.Mid); }, 15000), "B mở lại → tự nhận tin offline");
            Check(await Until(() => a.Pending(off.Mid).Count == 0), "Outbox của A trống sau khi B nhận");
            Check(new ChatStore(dirB).LoadHistory("u:" + idA).Count(x => x.Mid == m1.Mid) == 1, "Lịch sử B không trùng tin");
        }
        catch (Exception ex) { log.WriteLine("  LỖI  " + ex); fails++; }
        finally
        {
            a.Dispose(); b.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
        log.WriteLine(fails == 0 ? "TẤT CẢ ĐỀU ĐẠT" : $"{fails} mục LỖI");
        return fails == 0 ? 0 : 1;
    }
}
