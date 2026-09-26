# -*- coding: utf-8 -*-
"""Die Meldungen des Scan-Werkzeugs zur Aufloesung (contrib_scan.py), 25 Sprachen.

Am 2026-09-24 dazugekommen, als das Werkzeug jede Aufloesung ab 720p annahm statt
nur 1920x1080. Platzhalter {0}..{4} muessen in jeder Sprache stehen bleiben; {4:.2f}
ist eine Python-Formatangabe und wird unveraendert uebernommen.

Nicht von Muttersprachlern geprueft.
"""

A = "Game area {0}x{1} at {2},{3}, read at 1920x1080 (scale {4:.2f})."
B = ("Note: the game area is {0}x{1}, not 16:9. The scan uses its centred 16:9 part; "
     "menu positions at this aspect ratio are an assumption.")
C = ("The game area is {0}x{1}. Below 1280x720 the leaderboard text is too small to "
     "read reliably -- use a larger window or resolution.")

T = {
 A: {
  "de": "Spielflaeche {0}x{1} bei {2},{3}, gelesen als 1920x1080 (Faktor {4:.2f}).",
  "fr": "Zone de jeu {0}x{1} en {2},{3}, lue en 1920x1080 (échelle {4:.2f}).",
  "es": "Área de juego {0}x{1} en {2},{3}, leída a 1920x1080 (escala {4:.2f}).",
  "it": "Area di gioco {0}x{1} in {2},{3}, letta a 1920x1080 (scala {4:.2f}).",
  "pt": "Área de jogo {0}x{1} em {2},{3}, lida a 1920x1080 (escala {4:.2f}).",
  "nl": "Speelgebied {0}x{1} op {2},{3}, gelezen als 1920x1080 (schaal {4:.2f}).",
  "pl": "Obszar gry {0}x{1} w {2},{3}, odczytany jako 1920x1080 (skala {4:.2f}).",
  "sv": "Spelyta {0}x{1} vid {2},{3}, läst som 1920x1080 (skala {4:.2f}).",
  "da": "Spilområde {0}x{1} ved {2},{3}, læst som 1920x1080 (skala {4:.2f}).",
  "fi": "Pelialue {0}x{1} kohdassa {2},{3}, luettu kokoon 1920x1080 (mittakaava {4:.2f}).",
  "cs": "Herní plocha {0}x{1} na {2},{3}, čtena jako 1920x1080 (měřítko {4:.2f}).",
  "hu": "Játékterület {0}x{1} itt: {2},{3}, 1920x1080-ként olvasva (lépték {4:.2f}).",
  "ro": "Zona de joc {0}x{1} la {2},{3}, citită la 1920x1080 (scară {4:.2f}).",
  "el": "Περιοχή παιχνιδιού {0}x{1} στο {2},{3}, ανάγνωση ως 1920x1080 (κλίμακα {4:.2f}).",
  "ru": "Область игры {0}x{1} в {2},{3}, читается как 1920x1080 (масштаб {4:.2f}).",
  "tr": "Oyun alanı {0}x{1}, konum {2},{3}; 1920x1080 olarak okunuyor (ölçek {4:.2f}).",
  "id": "Area game {0}x{1} di {2},{3}, dibaca sebagai 1920x1080 (skala {4:.2f}).",
  "ms": "Kawasan permainan {0}x{1} di {2},{3}, dibaca sebagai 1920x1080 (skala {4:.2f}).",
  "vi": "Vùng trò chơi {0}x{1} tại {2},{3}, đọc ở 1920x1080 (tỉ lệ {4:.2f}).",
  "th": "พื้นที่เกม {0}x{1} ที่ {2},{3} อ่านเป็น 1920x1080 (สเกล {4:.2f})",
  "ja": "ゲーム領域 {0}x{1}（位置 {2},{3}）、1920x1080 として読み取り（倍率 {4:.2f}）。",
  "ko": "게임 영역 {0}x{1}, 위치 {2},{3}, 1920x1080으로 읽음 (배율 {4:.2f}).",
  "zh-Hans": "游戏区域 {0}x{1}，位于 {2},{3}，按 1920x1080 读取（比例 {4:.2f}）。",
  "zh-Hant": "遊戲區域 {0}x{1}，位於 {2},{3}，按 1920x1080 讀取（比例 {4:.2f}）。",
 },
 B: {
  "de": "Hinweis: Die Spielflaeche ist {0}x{1}, nicht 16:9. Der Scan nutzt ihren mittigen 16:9-Teil; die Lage der Menues bei diesem Seitenverhaeltnis ist eine Annahme.",
  "fr": "Remarque : la zone de jeu fait {0}x{1}, pas 16:9. Le scan utilise sa partie centrale en 16:9 ; la position des menus dans ce format est une hypothèse.",
  "es": "Nota: el área de juego es {0}x{1}, no 16:9. El escaneo usa su parte central 16:9; la posición de los menús en esta proporción es una suposición.",
  "it": "Nota: l'area di gioco è {0}x{1}, non 16:9. La scansione usa la parte centrale 16:9; la posizione dei menu con questo formato è un'ipotesi.",
  "pt": "Nota: a área de jogo é {0}x{1}, não 16:9. A análise usa a parte central 16:9; a posição dos menus nesta proporção é uma suposição.",
  "nl": "Let op: het speelgebied is {0}x{1}, niet 16:9. De scan gebruikt het middelste 16:9-deel; de plaats van de menu's bij deze verhouding is een aanname.",
  "pl": "Uwaga: obszar gry ma {0}x{1}, a nie 16:9. Skan używa środkowej części 16:9; położenie menu przy tych proporcjach jest założeniem.",
  "sv": "Obs: spelytan är {0}x{1}, inte 16:9. Skanningen använder dess mittersta 16:9-del; menyernas läge i detta format är ett antagande.",
  "da": "Bemærk: spilområdet er {0}x{1}, ikke 16:9. Scanningen bruger den midterste 16:9-del; menuernes placering i dette format er en antagelse.",
  "fi": "Huom: pelialue on {0}x{1}, ei 16:9. Skannaus käyttää sen keskimmäistä 16:9-osaa; valikoiden sijainti tällä kuvasuhteella on oletus.",
  "cs": "Pozor: herní plocha je {0}x{1}, ne 16:9. Sken používá její středovou část 16:9; poloha nabídek při tomto poměru je předpoklad.",
  "hu": "Megjegyzés: a játékterület {0}x{1}, nem 16:9. A beolvasás a középső 16:9-es részt használja; a menük helye ennél a képaránynál feltételezés.",
  "ro": "Notă: zona de joc este {0}x{1}, nu 16:9. Scanarea folosește partea centrală 16:9; poziția meniurilor la acest raport este o presupunere.",
  "el": "Σημείωση: η περιοχή παιχνιδιού είναι {0}x{1}, όχι 16:9. Η σάρωση χρησιμοποιεί το κεντρικό 16:9 τμήμα· η θέση των μενού σε αυτή την αναλογία είναι υπόθεση.",
  "ru": "Примечание: область игры {0}x{1}, а не 16:9. Сканирование использует её центральную часть 16:9; положение меню при таком соотношении — предположение.",
  "tr": "Not: oyun alanı {0}x{1}, 16:9 değil. Tarama ortadaki 16:9 bölümünü kullanır; bu oranda menülerin konumu bir varsayımdır.",
  "id": "Catatan: area game {0}x{1}, bukan 16:9. Pemindaian memakai bagian tengah 16:9; posisi menu pada rasio ini adalah asumsi.",
  "ms": "Nota: kawasan permainan ialah {0}x{1}, bukan 16:9. Imbasan menggunakan bahagian tengah 16:9; kedudukan menu pada nisbah ini ialah andaian.",
  "vi": "Lưu ý: vùng trò chơi là {0}x{1}, không phải 16:9. Quá trình quét dùng phần 16:9 ở giữa; vị trí menu ở tỉ lệ này chỉ là giả định.",
  "th": "หมายเหตุ: พื้นที่เกมคือ {0}x{1} ไม่ใช่ 16:9 การสแกนใช้ส่วน 16:9 ตรงกลาง ตำแหน่งเมนูในอัตราส่วนนี้เป็นเพียงการสันนิษฐาน",
  "ja": "注意：ゲーム領域は {0}x{1} で 16:9 ではありません。スキャンは中央の 16:9 部分を使います。この比率でのメニュー位置は推定です。",
  "ko": "참고: 게임 영역이 {0}x{1}로 16:9가 아닙니다. 스캔은 가운데 16:9 부분을 사용하며, 이 비율에서 메뉴 위치는 추정입니다.",
  "zh-Hans": "注意：游戏区域为 {0}x{1}，不是 16:9。扫描使用其居中的 16:9 部分；此比例下的菜单位置只是假设。",
  "zh-Hant": "注意：遊戲區域為 {0}x{1}，不是 16:9。掃描使用其置中的 16:9 部分；此比例下的選單位置只是假設。",
 },
 C: {
  "de": "Die Spielflaeche ist {0}x{1}. Unter 1280x720 ist die Schrift der Bestenliste zu klein, um sie sicher zu lesen -- ein groesseres Fenster oder eine hoehere Aufloesung verwenden.",
  "fr": "La zone de jeu fait {0}x{1}. En dessous de 1280x720, le texte du classement est trop petit pour être lu de façon fiable -- utilisez une fenêtre plus grande ou une résolution plus élevée.",
  "es": "El área de juego es {0}x{1}. Por debajo de 1280x720 el texto de la clasificación es demasiado pequeño para leerlo con fiabilidad: usa una ventana más grande o una resolución mayor.",
  "it": "L'area di gioco è {0}x{1}. Sotto 1280x720 il testo della classifica è troppo piccolo per una lettura affidabile: usa una finestra più grande o una risoluzione maggiore.",
  "pt": "A área de jogo é {0}x{1}. Abaixo de 1280x720 o texto da classificação é pequeno demais para ser lido com fiabilidade -- use uma janela maior ou uma resolução mais alta.",
  "nl": "Het speelgebied is {0}x{1}. Onder 1280x720 is de tekst van de ranglijst te klein om betrouwbaar te lezen -- gebruik een groter venster of een hogere resolutie.",
  "pl": "Obszar gry ma {0}x{1}. Poniżej 1280x720 tekst rankingu jest za mały, by go pewnie odczytać -- użyj większego okna lub wyższej rozdzielczości.",
  "sv": "Spelytan är {0}x{1}. Under 1280x720 är topplistans text för liten för att läsas säkert -- använd ett större fönster eller en högre upplösning.",
  "da": "Spilområdet er {0}x{1}. Under 1280x720 er ranglistens tekst for lille til at blive læst sikkert -- brug et større vindue eller en højere opløsning.",
  "fi": "Pelialue on {0}x{1}. Alle 1280x720 tulostaulun teksti on liian pieni luotettavaan lukemiseen -- käytä suurempaa ikkunaa tai tarkkuutta.",
  "cs": "Herní plocha je {0}x{1}. Pod 1280x720 je text žebříčku příliš malý pro spolehlivé čtení -- použijte větší okno nebo vyšší rozlišení.",
  "hu": "A játékterület {0}x{1}. 1280x720 alatt a ranglista szövege túl kicsi a megbízható olvasáshoz -- használj nagyobb ablakot vagy felbontást.",
  "ro": "Zona de joc este {0}x{1}. Sub 1280x720 textul clasamentului este prea mic pentru a fi citit sigur -- folosește o fereastră mai mare sau o rezoluție mai mare.",
  "el": "Η περιοχή παιχνιδιού είναι {0}x{1}. Κάτω από 1280x720 το κείμενο της κατάταξης είναι πολύ μικρό για αξιόπιστη ανάγνωση -- χρησιμοποιήστε μεγαλύτερο παράθυρο ή υψηλότερη ανάλυση.",
  "ru": "Область игры {0}x{1}. Ниже 1280x720 текст таблицы слишком мелкий для надёжного чтения — используйте окно побольше или более высокое разрешение.",
  "tr": "Oyun alanı {0}x{1}. 1280x720'nin altında sıralama metni güvenilir okunamayacak kadar küçüktür -- daha büyük bir pencere veya daha yüksek çözünürlük kullanın.",
  "id": "Area game {0}x{1}. Di bawah 1280x720 teks papan peringkat terlalu kecil untuk dibaca dengan andal -- gunakan jendela lebih besar atau resolusi lebih tinggi.",
  "ms": "Kawasan permainan ialah {0}x{1}. Di bawah 1280x720 teks papan pendahulu terlalu kecil untuk dibaca dengan tepat -- gunakan tetingkap lebih besar atau resolusi lebih tinggi.",
  "vi": "Vùng trò chơi là {0}x{1}. Dưới 1280x720 chữ trên bảng xếp hạng quá nhỏ để đọc tin cậy -- hãy dùng cửa sổ lớn hơn hoặc độ phân giải cao hơn.",
  "th": "พื้นที่เกมคือ {0}x{1} ต่ำกว่า 1280x720 ตัวอักษรในกระดานอันดับเล็กเกินกว่าจะอ่านได้แม่นยำ -- ใช้หน้าต่างที่ใหญ่ขึ้นหรือความละเอียดที่สูงขึ้น",
  "ja": "ゲーム領域は {0}x{1} です。1280x720 未満ではランキングの文字が小さすぎて確実に読めません。より大きなウィンドウか高い解像度を使ってください。",
  "ko": "게임 영역이 {0}x{1}입니다. 1280x720 미만에서는 순위표 글자가 너무 작아 정확히 읽을 수 없습니다. 더 큰 창이나 높은 해상도를 사용하세요.",
  "zh-Hans": "游戏区域为 {0}x{1}。低于 1280x720 时排行榜文字太小，无法可靠读取——请使用更大的窗口或更高的分辨率。",
  "zh-Hant": "遊戲區域為 {0}x{1}。低於 1280x720 時排行榜文字太小，無法可靠讀取——請使用更大的視窗或更高的解析度。",
 },
}

CODES = ["de","fr","es","it","pt","nl","pl","sv","da","fi","cs","hu","ro","el",
         "ru","tr","id","ms","vi","th","ja","ko","zh-Hans","zh-Hant"]

ZUSATZ = {code: {k: v[code] for k, v in T.items()} for code in CODES}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
