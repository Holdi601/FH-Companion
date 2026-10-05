# -*- coding: utf-8 -*-
"""Nur eine Automatik fuehrt das Spiel (2026-10-04), 25 Sprachen, nicht von
Muttersprachlern geprueft. "Auktionshaus" wie in auktionshaus.py."""

K = {
    "sperre": "Another automation (board scan or auction check) is driving the game. Nothing was pressed.",
    "vorn": "Auction house: the game could not be brought to the front."
}

_UEBERSETZT = {
 "de": {
    "sperre": "Eine andere Automatik (Bestenlisten-Scan oder Auktionsprüfung) steuert gerade das Spiel. Es wurde nichts gedrückt.",
    "vorn": "Auktionshaus: das Spiel ließ sich nicht nach vorn holen."},
 "fr": {
    "sperre": "Une autre automatisation (scan de classement ou vérification des enchères) pilote déjà le jeu. Aucune touche n'a été pressée.",
    "vorn": "Salle des ventes : impossible de mettre le jeu au premier plan."},
 "es": {
    "sperre": "Otra automatización (escaneo de clasificaciones o revisión de subastas) está controlando el juego. No se pulsó nada.",
    "vorn": "Casa de subastas: no se pudo traer el juego al frente."},
 "it": {
    "sperre": "Un'altra automazione (scansione classifiche o controllo aste) sta già controllando il gioco. Non è stato premuto nulla.",
    "vorn": "Casa d'aste: impossibile portare il gioco in primo piano."},
 "pt": {
    "sperre": "Outra automatização (leitura de classificações ou verificação de leilões) está a controlar o jogo. Nada foi premido.",
    "vorn": "Leiloeira: não foi possível trazer o jogo para a frente."},
 "nl": {
    "sperre": "Een andere automatisering (klassementscan of veilingcontrole) bestuurt het spel al. Er is niets ingedrukt.",
    "vorn": "Veilinghuis: het spel kon niet naar voren worden gehaald."},
 "pl": {
    "sperre": "Inna automatyzacja (skan tabel wyników lub sprawdzanie aukcji) steruje już grą. Nic nie zostało naciśnięte.",
    "vorn": "Dom aukcyjny: nie udało się przenieść gry na pierwszy plan."},
 "cs": {
    "sperre": "Hru už řídí jiná automatika (skenování žebříčků nebo kontrola aukcí). Nic nebylo stisknuto.",
    "vorn": "Aukční síň: hru se nepodařilo přenést do popředí."},
 "da": {
    "sperre": "En anden automatik (rangliste-scanning eller auktionstjek) styrer allerede spillet. Der blev ikke trykket på noget.",
    "vorn": "Auktionshus: spillet kunne ikke hentes frem."},
 "sv": {
    "sperre": "En annan automatik (topplisteskanning eller auktionskontroll) styr redan spelet. Inget trycktes.",
    "vorn": "Auktionshuset: spelet kunde inte tas fram."},
 "nb": {
    "sperre": "En annen automatikk (topplisteskanning eller auksjonssjekk) styrer allerede spillet. Ingenting ble trykket.",
    "vorn": "Auksjonshus: spillet kunne ikke hentes frem."},
 "fi": {
    "sperre": "Toinen automaatio (tulostaulukon luku tai huutokauppojen tarkistus) ohjaa jo peliä. Mitään ei painettu.",
    "vorn": "Huutokauppa: peliä ei saatu etualalle."},
 "hu": {
    "sperre": "Egy másik automatika (ranglista-beolvasás vagy aukcióellenőrzés) már vezérli a játékot. Semmi nem lett lenyomva.",
    "vorn": "Aukciósház: a játékot nem sikerült előtérbe hozni."},
 "ro": {
    "sperre": "O altă automatizare (scanare clasamente sau verificare licitații) controlează deja jocul. Nu s-a apăsat nimic.",
    "vorn": "Casa de licitații: jocul nu a putut fi adus în prim-plan."},
 "el": {
    "sperre": "Ένας άλλος αυτοματισμός (σάρωση βαθμολογιών ή έλεγχος δημοπρασιών) ελέγχει ήδη το παιχνίδι. Δεν πατήθηκε τίποτα.",
    "vorn": "Οίκος δημοπρασιών: το παιχνίδι δεν μπόρεσε να έρθει μπροστά."},
 "ru": {
    "sperre": "Игрой уже управляет другая автоматика (сканирование таблиц или проверка аукциона). Ничего не нажато.",
    "vorn": "Аукционный дом: не удалось вывести игру на передний план."},
 "tr": {
    "sperre": "Oyunu zaten başka bir otomasyon (sıralama taraması veya müzayede kontrolü) yönetiyor. Hiçbir tuşa basılmadı.",
    "vorn": "Müzayede Evi: oyun öne getirilemedi."},
 "id": {
    "sperre": "Otomasi lain (pemindaian papan peringkat atau pemeriksaan lelang) sedang mengendalikan game. Tidak ada yang ditekan.",
    "vorn": "Rumah lelang: game tidak dapat dibawa ke depan."},
 "ms": {
    "sperre": "Automasi lain (imbasan papan pendahulu atau semakan lelongan) sedang mengawal permainan. Tiada apa-apa ditekan.",
    "vorn": "Rumah lelong: permainan tidak dapat dibawa ke hadapan."},
 "vi": {
    "sperre": "Một chế độ tự động khác (quét bảng xếp hạng hoặc kiểm tra đấu giá) đang điều khiển trò chơi. Không có phím nào được nhấn.",
    "vorn": "Nhà đấu giá: không thể đưa trò chơi lên trước."},
 "th": {
    "sperre": "มีระบบอัตโนมัติอื่น (สแกนกระดานผู้นำ หรือตรวจสอบการประมูล) กำลังควบคุมเกมอยู่ ไม่มีการกดปุ่มใด",
    "vorn": "โรงประมูล: ไม่สามารถนำเกมขึ้นมาด้านหน้าได้"},
 "ja": {
    "sperre": "別の自動操作（リーダーボードのスキャン、オークションの確認）がゲームを操作中です。何も押していません。",
    "vorn": "オークションハウス：ゲームを前面に出せませんでした。"},
 "ko": {
    "sperre": "다른 자동화(리더보드 스캔 또는 경매 확인)가 이미 게임을 조작하고 있습니다. 아무 키도 누르지 않았습니다.",
    "vorn": "경매장: 게임을 앞으로 가져올 수 없습니다."},
 "zh-Hans": {
    "sperre": "另一个自动操作（排行榜扫描或拍卖检查）正在控制游戏。未按下任何按键。",
    "vorn": "拍卖场：无法将游戏切换到前台。"},
 "zh-Hant": {
    "sperre": "另一個自動操作（排行榜掃描或拍賣檢查）正在控制遊戲。未按下任何按鍵。",
    "vorn": "拍賣場：無法將遊戲切換到前景。"},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _text in _saetze.items():
        for _i in range(6):
            assert ("{%d}" % _i in K[_id]) == ("{%d}" % _i in _text), (_code, _id, _i)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
