# -*- coding: utf-8 -*-
"""Die Saetze des Scan-Werkzeugs -- asiatische Sprachen.

Aufbau wie `scanner_europa.py`: die englischen Schluessel stehen einmal in `NEU`,
die Sprachen liefern nur die Uebersetzungen in derselben Reihenfolge.

Die Platzhalter {runs}, {rows}, {code}, {server}, {error}, {detail}, {path} und
{key} muessen unveraendert stehen bleiben.

Diese Uebersetzungen sind nicht von Muttersprachlern geprueft.
"""

from pathlib import Path
import importlib.util

# Die Schluesselliste nicht zweimal schreiben: sie steht schon in scanner_europa.
_spec = importlib.util.spec_from_file_location(
    "scanner_europa", Path(__file__).with_name("scanner_europa.py"))
_europa = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_europa)
NEU = _europa.NEU

_UEBERSETZT = {}

_UEBERSETZT["zh-Hans"] = [
    "完成：{runs} 次运行，{rows} 行",
    "示例：",
    "如果你确定仍然可行：--skip-checks",
    "你拿到的那些信息就写在里面：",
    "配置里没有服务器 — 请手动发送文件。",
    "没有可打包的内容 — 没有任何排行榜跑完。",
    "同时运行的 OCR 读取器数量。多一些更快，但读取器池在主机上曾在十二个时崩溃过。",
    "在这台电脑上扫描排行榜并交回结果。",
    "文件就在这里，可以手动发送：",
    "扫描器以代码 {code} 结束。已经完成的部分仍会被打包。",
    "这台电脑现在归扫描器所用。不要点击，不要打字，",
    "这样还不能开始：",
    "要上传请用：--upload，或者直接把文件发过来。",
    "正在上传到 {server} ...",
    "给接收者的备注",
    "已接受：{runs} 次运行中的 {rows} 行",
    "并让 Forza 保持在最前。按 Ctrl+C 可停止。",
    "用逗号分隔，例如 D,C,B,A,S1,S2,R",
    "不扫描，只打包已有的内容",
    "例如 0-3 或 5,7,9 — 轮播的顺序。",
    "未上传：{error}",
    "打包现有内容，不扫描",
    "被拒绝（{code}）：{detail}",
    "之后再发送",
    "找不到 {path}。",
    "{path}：缺少 {key}",
    "{rows} 行",
]

_UEBERSETZT["zh"] = list(_UEBERSETZT["zh-Hans"])

_UEBERSETZT["zh-Hant"] = [
    "完成：{runs} 次執行，{rows} 列",
    "範例：",
    "如果你確定仍然可行：--skip-checks",
    "你拿到的那些資訊就寫在裡面：",
    "設定裡沒有伺服器 — 請手動傳送檔案。",
    "沒有可封裝的內容 — 沒有任何排行榜跑完。",
    "同時執行的 OCR 讀取器數量。多一些比較快，但讀取器集區在主機上曾在十二個時當掉。",
    "在這台電腦上掃描排行榜並交回結果。",
    "檔案就在這裡，可以手動傳送：",
    "掃描器以代碼 {code} 結束。已經完成的部分仍會被封裝。",
    "這台電腦現在歸掃描器所用。不要點擊，不要打字，",
    "這樣還不能開始：",
    "要上傳請用：--upload，或直接把檔案傳過來。",
    "正在上傳到 {server} ...",
    "給收件者的備註",
    "已接受：{runs} 次執行中的 {rows} 列",
    "並讓 Forza 保持在最上層。按 Ctrl+C 可停止。",
    "以逗號分隔，例如 D,C,B,A,S1,S2,R",
    "不掃描，只封裝既有的內容",
    "例如 0-3 或 5,7,9 — 輪播的順序。",
    "未上傳：{error}",
    "封裝既有內容，不掃描",
    "遭拒絕（{code}）：{detail}",
    "之後再傳送",
    "找不到 {path}。",
    "{path}：缺少 {key}",
    "{rows} 列",
]

_UEBERSETZT["ja"] = [
    "完了：{runs} 回の実行、{rows} 行",
    "例：",
    "それでも動くと確信できる場合は：--skip-checks",
    "受け取った情報はそこに書きます：",
    "設定にサーバーがありません — ファイルを手で送ってください。",
    "詰めるものがありません — 完了したボードが一つもありません。",
    "同時に動かす OCR 読み取り数。多いほど速くなりますが、読み取りプールはメインの"
    "マシンで十二のときに落ちたことがあります。",
    "このパソコンでリーダーボードを読み取り、結果を提出します。",
    "ファイルはここにあり、手で送ることもできます：",
    "スキャナーはコード {code} で終了しました。終わった分はそれでも詰めます。",
    "このパソコンは今スキャナーのものです。クリックせず、入力せず、",
    "このままではまだ始められません：",
    "アップロードするには：--upload、またはファイルをそのまま送ってください。",
    "{server} へアップロード中 ...",
    "受け取る人へのメモ",
    "受理：{runs} 回の実行で {rows} 行",
    "Forza を手前に置いたままにしてください。Ctrl+C で止まります。",
    "カンマ区切り、例：D,C,B,A,S1,S2,R",
    "読み取らず、すでにあるものだけを詰める",
    "例：0-3 または 5,7,9 — カルーセルの順番。",
    "アップロードされませんでした：{error}",
    "あるものを詰める。読み取りはしない",
    "拒否されました（{code}）：{detail}",
    "あとで送る",
    "{path} がありません。",
    "{path}：{key} がありません",
    "{rows} 行",
]

_UEBERSETZT["ko"] = [
    "완료: {runs}회 실행, {rows}줄",
    "예:",
    "그래도 된다고 확신하면: --skip-checks",
    "받은 정보를 그 안에 적습니다:",
    "설정에 서버가 없습니다 — 파일을 직접 보내세요.",
    "담을 것이 없습니다 — 완료된 순위표가 없습니다.",
    "동시에 돌릴 OCR 판독기 수. 많을수록 빠르지만, 판독기 풀이 메인 컴퓨터에서 "
    "열두 개일 때 한 번 죽은 적이 있습니다.",
    "이 컴퓨터에서 순위표를 읽고 결과를 제출합니다.",
    "파일은 여기 있으며 직접 보낼 수도 있습니다:",
    "판독기가 코드 {code}(으)로 끝났습니다. 끝난 것은 그래도 담습니다.",
    "이제 이 컴퓨터는 판독기의 것입니다. 클릭하지 말고, 입력하지 말고,",
    "이대로는 아직 시작할 수 없습니다:",
    "올리려면: --upload, 또는 파일을 그냥 보내세요.",
    "{server}(으)로 올리는 중 ...",
    "받는 사람에게 남기는 메모",
    "받아들임: {runs}회 실행에서 {rows}줄",
    "Forza를 앞에 두세요. Ctrl+C로 멈춥니다.",
    "쉼표로 구분, 예: D,C,B,A,S1,S2,R",
    "읽지 말고, 이미 있는 것만 담기",
    "예: 0-3 또는 5,7,9 — 회전 목록의 순서.",
    "올리지 못했습니다: {error}",
    "있는 것을 담고, 읽지 않기",
    "거부됨 ({code}): {detail}",
    "나중에 보내기",
    "{path}이(가) 없습니다.",
    "{path}: {key}이(가) 없습니다",
    "{rows}줄",
]

_UEBERSETZT["th"] = [
    "เสร็จแล้ว: {runs} รอบ, {rows} แถว",
    "ตัวอย่าง:",
    "ถ้าคุณแน่ใจว่ายังไปได้: --skip-checks",
    "ข้อมูลที่คุณได้รับต้องอยู่ในนั้น:",
    "ไม่มีเซิร์ฟเวอร์ในการตั้งค่า — ส่งไฟล์ด้วยมือ",
    "ไม่มีอะไรให้แพ็ก — ไม่มีตารางใดเสร็จเลย",
    "จำนวนตัวอ่าน OCR พร้อมกัน มากกว่าจะเร็วกว่า แต่กลุ่มตัวอ่านเคยล่มที่สิบสอง"
    "บนเครื่องหลักมาแล้ว",
    "สแกนตารางอันดับบนเครื่องนี้แล้วส่งผลลัพธ์",
    "ไฟล์อยู่ที่นี่ และส่งด้วยมือได้:",
    "ตัวสแกนจบด้วยรหัส {code} สิ่งที่เสร็จแล้วยังถูกแพ็กอยู่ดี",
    "ตอนนี้เครื่องเป็นของตัวสแกน อย่าคลิก อย่าพิมพ์",
    "แบบนี้ยังเริ่มไม่ได้:",
    "ถ้าจะอัปโหลด: --upload หรือส่งไฟล์ไปเฉย ๆ",
    "กำลังอัปโหลดไปที่ {server} ...",
    "ข้อความถึงผู้รับ",
    "รับแล้ว: {rows} แถวใน {runs} รอบ",
    "และปล่อยให้ Forza อยู่หน้าสุด กด Ctrl+C เพื่อหยุด",
    "คั่นด้วยจุลภาค เช่น D,C,B,A,S1,S2,R",
    "ไม่ต้องสแกน แพ็กเฉพาะที่มีอยู่แล้ว",
    "เช่น 0-3 หรือ 5,7,9 — ลำดับของวงล้อ",
    "ไม่ได้อัปโหลด: {error}",
    "แพ็กสิ่งที่มี ไม่ต้องสแกน",
    "ถูกปฏิเสธ ({code}): {detail}",
    "ส่งทีหลัง",
    "ไม่มี {path}",
    "{path}: ขาด {key}",
    "{rows} แถว",
]

_UEBERSETZT["vi"] = [
    "Xong: {runs} lượt chạy, {rows} dòng",
    "Ví dụ:",
    "Nếu bạn chắc chắn nó vẫn chạy được: --skip-checks",
    "Những thông tin bạn được đưa nằm trong đó:",
    "Không có máy chủ trong cấu hình — hãy gửi tệp bằng tay.",
    "Không có gì để đóng gói — chưa bảng nào chạy xong.",
    "Số bộ đọc OCR cùng lúc. Nhiều hơn thì nhanh hơn, nhưng nhóm bộ đọc đã từng sập "
    "ở mười hai trên máy chính.",
    "Quét bảng xếp hạng trên MÁY NÀY và nộp lại kết quả.",
    "Tệp nằm ở đây và có thể gửi bằng tay:",
    "Bộ quét kết thúc với mã {code}. Phần nào xong vẫn được đóng gói.",
    "Máy này giờ thuộc về bộ quét. Đừng bấm, đừng gõ,",
    "Như thế này thì chưa bắt đầu được:",
    "Để tải lên: --upload, hoặc cứ gửi tệp đi.",
    "Đang tải lên {server} ...",
    "một lời nhắn cho người nhận",
    "đã nhận: {rows} dòng trong {runs} lượt chạy",
    "và để Forza ở phía trước. Ctrl+C dừng lại.",
    "cách nhau bằng dấu phẩy, ví dụ D,C,B,A,S1,S2,R",
    "đừng quét, chỉ đóng gói những gì đã có",
    "ví dụ 0-3 hoặc 5,7,9 — thứ tự của vòng quay.",
    "chưa tải lên: {error}",
    "đóng gói những gì có, không quét",
    "bị từ chối ({code}): {detail}",
    "gửi sau",
    "Không có {path}.",
    "{path}: thiếu {key}",
    "{rows} dòng",
]

_UEBERSETZT["id"] = [
    "Selesai: {runs} jalan, {rows} baris",
    "Contoh:",
    "Kalau kamu yakin tetap bisa: --skip-checks",
    "Di situlah keterangan yang kamu terima ditulis:",
    "Tidak ada server di konfigurasi — kirim berkasnya sendiri.",
    "Tidak ada yang bisa dibungkus — tak ada papan yang selesai.",
    "Pembaca OCR sekaligus. Lebih banyak lebih cepat, tetapi kumpulan pembaca sudah "
    "pernah tumbang pada dua belas di mesin utama.",
    "Memindai papan peringkat di KOMPUTER INI dan menyerahkan hasilnya.",
    "Berkasnya ada di sini dan bisa dikirim sendiri:",
    "Pemindai berakhir dengan kode {code}. Yang sudah selesai tetap dibungkus.",
    "Komputer ini sekarang milik pemindai. Jangan klik, jangan mengetik,",
    "Begini belum bisa mulai:",
    "Untuk mengunggah: --upload, atau kirim saja berkasnya.",
    "Mengunggah ke {server} ...",
    "catatan untuk yang menerima",
    "diterima: {rows} baris dalam {runs} jalan",
    "dan biarkan Forza di depan. Ctrl+C menghentikannya.",
    "dipisah koma, mis. D,C,B,A,S1,S2,R",
    "jangan memindai, hanya bungkus yang sudah ada",
    "mis. 0-3 atau 5,7,9 — urutan korselnya.",
    "tidak terunggah: {error}",
    "bungkus yang ada, tanpa memindai",
    "ditolak ({code}): {detail}",
    "kirim sesudahnya",
    "{path} tidak ada.",
    "{path}: {key} tidak ada",
    "{rows} baris",
]

_UEBERSETZT["ms"] = [
    "Siap: {runs} larian, {rows} baris",
    "Contoh:",
    "Kalau anda pasti ia tetap boleh: --skip-checks",
    "Di situlah butiran yang anda terima ditulis:",
    "Tiada pelayan dalam tetapan — hantar failnya sendiri.",
    "Tiada apa hendak dibungkus — tiada papan yang selesai.",
    "Pembaca OCR serentak. Lebih banyak lebih laju, tetapi kolam pembaca pernah "
    "tumbang pada dua belas di mesin utama.",
    "Mengimbas papan kedudukan pada KOMPUTER INI dan menyerahkan hasilnya.",
    "Fail ada di sini dan boleh dihantar sendiri:",
    "Pengimbas berakhir dengan kod {code}. Yang sudah siap tetap dibungkus.",
    "Komputer ini kini milik pengimbas. Jangan klik, jangan taip,",
    "Begini belum boleh bermula:",
    "Untuk memuat naik: --upload, atau hantar sahaja failnya.",
    "Memuat naik ke {server} ...",
    "nota untuk penerima",
    "diterima: {rows} baris dalam {runs} larian",
    "dan biarkan Forza di hadapan. Ctrl+C menghentikannya.",
    "dipisah koma, cth. D,C,B,A,S1,S2,R",
    "jangan imbas, bungkus sahaja yang sedia ada",
    "cth. 0-3 atau 5,7,9 — susunan karusel.",
    "tidak dimuat naik: {error}",
    "bungkus yang ada, tanpa mengimbas",
    "ditolak ({code}): {detail}",
    "hantar selepas itu",
    "{path} tiada.",
    "{path}: {key} tiada",
    "{rows} baris",
]

ZUSATZ = {}
for _code, _liste in _UEBERSETZT.items():
    if len(_liste) != len(NEU):
        raise SystemExit(
            f"{_code}: {len(_liste)} Eintraege, gebraucht werden {len(NEU)}")
    ZUSATZ[_code] = dict(zip(NEU, _liste))
