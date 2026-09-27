# -*- coding: utf-8 -*-
"""Wartende Runden -- Zeile und Knopf im Rivals-Tab (2026-09-27), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = (
    "Discard waiting laps",
    "Discard {0} waiting lap(s)? They will not be submitted.",
    "{0} lap(s) beat the leaderboard and wait to be submitted. They are sent once submission is on, a gamertag is set and the server answers -- checked again against the leaderboard of that day.",
)

_UEBERSETZT = {
 "de": ("Wartende Runden verwerfen",
        "{0} wartende Runde(n) verwerfen? Sie werden dann nicht eingereicht.",
        "{0} Runde(n) schlagen die Bestenliste und warten auf das Einreichen. Sie gehen hinaus, sobald das Einreichen an ist, ein Gamertag eingetragen ist und der Server antwortet -- vorher noch einmal gegen die Bestenliste des Tages geprüft."),
 "fr": ("Supprimer les tours en attente",
        "Supprimer {0} tour(s) en attente ? Ils ne seront pas envoyés.",
        "{0} tour(s) battent le classement et attendent d'être envoyés. Ils partent dès que l'envoi est activé, qu'un gamertag est saisi et que le serveur répond -- vérifiés à nouveau contre le classement du jour."),
 "es": ("Descartar vueltas en espera",
        "¿Descartar {0} vuelta(s) en espera? No se enviarán.",
        "{0} vuelta(s) superan la clasificación y esperan a ser enviadas. Se envían en cuanto el envío esté activado, haya un gamertag y el servidor responda, comprobadas de nuevo con la clasificación de ese día."),
 "it": ("Scarta i giri in attesa",
        "Scartare {0} giro/i in attesa? Non verranno inviati.",
        "{0} giro/i battono la classifica e attendono di essere inviati. Partono non appena l'invio è attivo, è impostato un gamertag e il server risponde, verificati di nuovo con la classifica di quel giorno."),
 "pt": ("Descartar voltas em espera",
        "Descartar {0} volta(s) em espera? Elas não serão enviadas.",
        "{0} volta(s) superam a classificação e aguardam envio. São enviadas assim que o envio estiver ativado, houver um gamertag e o servidor responder -- verificadas de novo com a classificação desse dia."),
 "nl": ("Wachtende ronden weggooien",
        "{0} wachtende ronde(n) weggooien? Ze worden niet ingediend.",
        "{0} ronde(n) verslaan het klassement en wachten om ingediend te worden. Ze gaan eruit zodra indienen aan staat, er een gamertag is ingevuld en de server antwoordt -- eerst opnieuw getoetst aan het klassement van die dag."),
 "pl": ("Odrzuć oczekujące okrążenia",
        "Odrzucić oczekujące okrążenia ({0})? Nie zostaną wysłane.",
        "Okrążenia ({0}) pobijają ranking i czekają na wysłanie. Zostaną wysłane, gdy wysyłanie będzie włączone, ustawiony będzie gamertag i serwer odpowie -- wcześniej ponownie sprawdzone z rankingiem z tego dnia."),
 "sv": ("Släng väntande varv",
        "Släng {0} väntande varv? De skickas inte in.",
        "{0} varv slår topplistan och väntar på att skickas in. De skickas så snart inskickning är på, en gamertag är angiven och servern svarar -- kontrollerade igen mot den dagens topplista."),
 "da": ("Kassér ventende omgange",
        "Kassér {0} ventende omgang(e)? De bliver ikke indsendt.",
        "{0} omgang(e) slår ranglisten og venter på at blive indsendt. De sendes, så snart indsendelse er slået til, et gamertag er angivet og serveren svarer -- tjekket igen mod den dags rangliste."),
 "fi": ("Hylkää odottavat kierrokset",
        "Hylätäänkö {0} odottavaa kierrosta? Niitä ei lähetetä.",
        "{0} kierrosta lyö tulostaulun ja odottaa lähettämistä. Ne lähetetään heti, kun lähetys on päällä, pelaajatunnus on asetettu ja palvelin vastaa -- tarkistetaan uudelleen sen päivän tulostaulua vasten."),
 "cs": ("Zahodit čekající kola",
        "Zahodit čekající kola ({0})? Nebudou odeslána.",
        "Kola ({0}) překonávají žebříček a čekají na odeslání. Odejdou, jakmile bude odesílání zapnuté, bude zadán gamertag a server odpoví -- předtím znovu porovnána s žebříčkem toho dne."),
 "hu": ("Várakozó körök elvetése",
        "Elveted a(z) {0} várakozó kört? Nem lesznek beküldve.",
        "{0} kör megveri a ranglistát, és beküldésre vár. Akkor mennek el, amikor a beküldés be van kapcsolva, van megadott gamertag és a szerver válaszol -- előtte újra összevetve az aznapi ranglistával."),
 "ro": ("Renunță la turele în așteptare",
        "Renunți la {0} tur(e) în așteptare? Nu vor fi trimise.",
        "{0} tur(e) bat clasamentul și așteaptă să fie trimise. Pleacă de îndată ce trimiterea e activată, există un gamertag și serverul răspunde -- verificate din nou cu clasamentul din ziua respectivă."),
 "el": ("Απόρριψη γύρων σε αναμονή",
        "Απόρριψη {0} γύρων σε αναμονή; Δεν θα υποβληθούν.",
        "{0} γύροι ξεπερνούν την κατάταξη και περιμένουν να υποβληθούν. Στέλνονται μόλις ενεργοποιηθεί η υποβολή, οριστεί gamertag και απαντήσει ο διακομιστής -- αφού ελεγχθούν ξανά με την κατάταξη εκείνης της ημέρας."),
 "ru": ("Отбросить ожидающие круги",
        "Отбросить ожидающие круги ({0})? Они не будут отправлены.",
        "Круги ({0}) быстрее таблицы лидеров и ждут отправки. Они уйдут, как только отправка будет включена, указан gamertag и сервер ответит, — перед этим их снова сверят с таблицей лидеров на тот день."),
 "tr": ("Bekleyen turları at",
        "Bekleyen {0} tur atılsın mı? Gönderilmeyecekler.",
        "{0} tur sıralamayı geçiyor ve gönderilmeyi bekliyor. Gönderim açık olduğunda, bir gamertag girildiğinde ve sunucu yanıt verdiğinde gönderilirler -- öncesinde o günün sıralamasıyla yeniden karşılaştırılır."),
 "id": ("Buang lap yang menunggu",
        "Buang {0} lap yang menunggu? Lap itu tidak akan dikirim.",
        "{0} lap mengalahkan papan peringkat dan menunggu dikirim. Lap dikirim begitu pengiriman aktif, gamertag diisi, dan server menjawab -- diperiksa ulang terhadap papan peringkat hari itu."),
 "ms": ("Buang pusingan yang menunggu",
        "Buang {0} pusingan yang menunggu? Ia tidak akan dihantar.",
        "{0} pusingan mengalahkan papan pendahulu dan menunggu untuk dihantar. Ia dihantar sebaik penghantaran dihidupkan, gamertag ditetapkan dan pelayan menjawab -- disemak semula dengan papan pendahulu hari itu."),
 "vi": ("Bỏ các vòng đang chờ",
        "Bỏ {0} vòng đang chờ? Chúng sẽ không được gửi.",
        "{0} vòng vượt bảng xếp hạng và đang chờ gửi. Chúng được gửi ngay khi bật gửi, đã đặt gamertag và máy chủ phản hồi -- được kiểm tra lại với bảng xếp hạng của ngày hôm đó."),
 "th": ("ทิ้งรอบที่รออยู่",
        "ทิ้งรอบที่รออยู่ {0} รอบหรือไม่? รอบเหล่านี้จะไม่ถูกส่ง",
        "{0} รอบเร็วกว่าลีดเดอร์บอร์ดและกำลังรอส่ง จะส่งทันทีที่เปิดการส่ง ตั้งค่าเกมเมอร์แท็กแล้ว และเซิร์ฟเวอร์ตอบกลับ -- โดยตรวจสอบกับลีดเดอร์บอร์ดของวันนั้นอีกครั้ง"),
 "ja": ("待機中のラップを破棄",
        "待機中のラップ {0} 件を破棄しますか？送信されなくなります。",
        "{0} 件のラップがリーダーボードを上回り、送信を待っています。送信がオンで、ゲーマータグが設定され、サーバーが応答したときに送信されます。その前に、その日のリーダーボードと改めて照合します。"),
 "ko": ("대기 중인 랩 버리기",
        "대기 중인 랩 {0}개를 버릴까요? 제출되지 않습니다.",
        "랩 {0}개가 리더보드를 앞서 제출을 기다리고 있습니다. 제출이 켜져 있고 게이머태그가 설정되어 있으며 서버가 응답하면 전송됩니다. 그 전에 그날의 리더보드와 다시 비교합니다."),
 "zh-Hans": ("丢弃等待中的圈速",
        "丢弃 {0} 个等待中的圈速？它们将不会被提交。",
        "{0} 个圈速超过了排行榜，正在等待提交。一旦开启提交、设置了玩家代号且服务器有响应，就会发送——发送前会再次与当天的排行榜比较。"),
 "zh-Hant": ("捨棄等待中的圈速",
        "捨棄 {0} 個等待中的圈速？它們將不會被提交。",
        "{0} 個圈速超越了排行榜，正在等待提交。一旦開啟提交、設定了玩家代號且伺服器有回應，就會送出——送出前會再次與當天的排行榜比對。"),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))
    for _k, _s in zip(K, _saetze):
        assert ("{0}" in _k) == ("{0}" in _s), (_code, _s)

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
