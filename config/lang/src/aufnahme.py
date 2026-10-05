# -*- coding: utf-8 -*-
"""Aufnahmefenster und Overlays ueber dem Spiel (2026-09-28), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = (
    "Show overlays over the game",
    "Open the recording window",
    "Key colour for OBS",
    "Green",
    "Magenta",
    "Black",
    "overlay for recording",
)

_UEBERSETZT = {
 "de": ("Overlays über dem Spiel zeigen", "Aufnahmefenster öffnen", "Schlüsselfarbe für OBS", "Grün", "Magenta", "Schwarz", "Overlay für Aufnahmen"),
 "fr": ("Afficher les overlays sur le jeu", "Ouvrir la fenêtre d'enregistrement", "Couleur d'incrustation pour OBS", "Vert", "Magenta", "Noir", "overlay pour l'enregistrement"),
 "es": ("Mostrar overlays sobre el juego", "Abrir la ventana de grabación", "Color clave para OBS", "Verde", "Magenta", "Negro", "overlay para grabar"),
 "it": ("Mostra gli overlay sul gioco", "Apri la finestra di registrazione", "Colore chiave per OBS", "Verde", "Magenta", "Nero", "overlay per la registrazione"),
 "pt": ("Mostrar overlays sobre o jogo", "Abrir a janela de gravação", "Cor-chave para o OBS", "Verde", "Magenta", "Preto", "overlay para gravação"),
 "nl": ("Overlays over het spel tonen", "Opnamevenster openen", "Sleutelkleur voor OBS", "Groen", "Magenta", "Zwart", "overlay voor opnames"),
 "pl": ("Pokazuj nakładki nad grą", "Otwórz okno nagrywania", "Kolor kluczowania dla OBS", "Zielony", "Magenta", "Czarny", "nakładka do nagrywania"),
 "sv": ("Visa overlays över spelet", "Öppna inspelningsfönstret", "Nyckelfärg för OBS", "Grön", "Magenta", "Svart", "overlay för inspelning"),
 "da": ("Vis overlays over spillet", "Åbn optagevinduet", "Nøglefarve til OBS", "Grøn", "Magenta", "Sort", "overlay til optagelse"),
 "fi": ("Näytä overlayt pelin päällä", "Avaa tallennusikkuna", "Avainväri OBS:lle", "Vihreä", "Magenta", "Musta", "overlay tallennukseen"),
 "cs": ("Zobrazovat overlaye nad hrou", "Otevřít okno pro nahrávání", "Klíčovací barva pro OBS", "Zelená", "Purpurová", "Černá", "overlay pro nahrávání"),
 "hu": ("Overlayek mutatása a játék felett", "Felvételi ablak megnyitása", "Kulcsszín az OBS-hez", "Zöld", "Bíbor", "Fekete", "overlay felvételhez"),
 "ro": ("Afișează overlay-urile peste joc", "Deschide fereastra de înregistrare", "Culoare cheie pentru OBS", "Verde", "Magenta", "Negru", "overlay pentru înregistrare"),
 "el": ("Εμφάνιση overlay πάνω από το παιχνίδι", "Άνοιγμα παραθύρου εγγραφής", "Χρώμα κλειδιού για το OBS", "Πράσινο", "Ματζέντα", "Μαύρο", "overlay για εγγραφή"),
 "ru": ("Показывать оверлеи поверх игры", "Открыть окно для записи", "Цвет хромакея для OBS", "Зелёный", "Пурпурный", "Чёрный", "оверлей для записи"),
 "tr": ("Katmanları oyunun üzerinde göster", "Kayıt penceresini aç", "OBS için anahtar rengi", "Yeşil", "Macenta", "Siyah", "kayıt için katman"),
 "id": ("Tampilkan overlay di atas game", "Buka jendela rekaman", "Warna kunci untuk OBS", "Hijau", "Magenta", "Hitam", "overlay untuk rekaman"),
 "ms": ("Tunjukkan overlay di atas permainan", "Buka tetingkap rakaman", "Warna kunci untuk OBS", "Hijau", "Magenta", "Hitam", "overlay untuk rakaman"),
 "vi": ("Hiện lớp phủ trên trò chơi", "Mở cửa sổ ghi hình", "Màu khóa cho OBS", "Xanh lá", "Hồng tím", "Đen", "lớp phủ để ghi hình"),
 "th": ("แสดงโอเวอร์เลย์บนเกม", "เปิดหน้าต่างสำหรับบันทึก", "สีคีย์สำหรับ OBS", "เขียว", "ม่วงแดง", "ดำ", "โอเวอร์เลย์สำหรับบันทึก"),
 "ja": ("ゲームの上にオーバーレイを表示", "録画用ウィンドウを開く", "OBS 用のキーカラー", "グリーン", "マゼンタ", "ブラック", "録画用オーバーレイ"),
 "ko": ("게임 위에 오버레이 표시", "녹화용 창 열기", "OBS용 키 색상", "초록", "마젠타", "검정", "녹화용 오버레이"),
 "zh-Hans": ("在游戏上显示叠加层", "打开录制窗口", "OBS 抠像颜色", "绿色", "品红", "黑色", "录制用叠加层"),
 "zh-Hant": ("在遊戲上顯示疊加層", "開啟錄製視窗", "OBS 去背顏色", "綠色", "洋紅", "黑色", "錄製用疊加層"),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
