# Catalog khai bao tinh nang FBO/FAO/FAW
# ================================================================
# Moi file .txt trong thu muc nay = 1 nhom tinh nang
# (Ten file -> hien thi o cot Nhom)
#
# Format moi feature: bat dau bang dong [[FEATURE]], cac field key=value
# va ket thuc khi gap dong rong hoac block ke tiep.
#
# Key duoc ho tro:
#   title    = Ten tinh nang (bat buoc)
#   version  = Phien ban (SP24.2.2,...)
#   id       = Ma ID/ticket
#   products = San pham ho tro (FBO, FAO, FAW...)
#   include  = Dieu kien file Include. Lap lai duoc.
#              Cu phap:  include = Include\X.txt = INCLUDE   (hoac IGNORE)
#   option   = Dieu kien tren bang options (App DB).
#              Cu phap:  option = m_invoice_split = 1
#   wcommand = Dieu kien tren bang wcommand9 (Sys DB), check cot status theo xgroup.
#              Cu phap:  wcommand = SeparateInvoice = 1
#   guide    = Noi dung huong dan (dong dau)
#   guide+   = Noi dung huong dan (them dong)
#
# Comment bang #. Cot trang thai do tool tu tinh tu cac dieu kien:
#   - Da bat       : tat ca include khop expected
#   - Chua bat     : khong dieu kien include nao khop
#   - Mot phan     : mot so khop, mot so khong
#   - Chi DB       : chi co dieu kien option/wcommand (chua kiem tra)
#   - Khong xac dinh: thieu thong tin
#
# Co the bo sung tay file moi vao day, tool se nap lai khi nhan "Nap Catalog".
#
# BO SUNG (Bcode - man hinh Check Include):
#   entity   = Report\Config\Profile.ent | Conditional.Unit.Profile = INCLUDE
#              (dieu kien tren khai bao <!ENTITY % Ten "INCLUDE"> trong file .ent)
#   Dieu kien option / wcommand viet duoc ca dang cau UPDATE tu huong dan:
#   option   = UPDATE options SET val = 1 WHERE name = 'm_use_combo'
#   wcommand = UPDATE wcommand9 SET status = '1' WHERE xgroup = 'Combo'
#   Catalog cua ban: %AppData%\Bcode\includecheck\catalog\<nhom>.txt (cung ten nhom se thay ban co san).
