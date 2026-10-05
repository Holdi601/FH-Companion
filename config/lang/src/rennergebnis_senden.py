# -*- coding: utf-8 -*-
"""Reiter Rivals: das Einreichen schickt auch Horizon-Play-Rennergebnisse (2026-10-02). 25 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "hinweis": "With this on, the results of Horizon Play races are sent too: each driver's car, place and time -- no gamertags -- and a picture of the results screen as proof, which only the site's admin sees.",
}

_UEBERSETZT = {
 "de": {"hinweis": "Ist das an, gehen auch die Ergebnisse von Horizon-Play-Rennen hinaus: je Fahrer Auto, Platz und Zeit -- keine Gamertags -- und ein Bild des Ergebnisschirms als Beleg, das nur der Verwalter der Seite sieht."},
 "fr": {"hinweis": "Activé, les résultats des courses Horizon Play sont aussi envoyés : voiture, place et temps de chaque pilote -- sans gamertags -- et une image de l'écran des résultats comme preuve, que seul l'administrateur du site voit."},
 "es": {"hinweis": "Activado, también se envían los resultados de las carreras de Horizon Play: coche, puesto y tiempo de cada piloto -- sin gamertags -- y una imagen de la pantalla de resultados como prueba, que solo ve el administrador del sitio."},
 "it": {"hinweis": "Se attivo, vengono inviati anche i risultati delle gare Horizon Play: auto, posizione e tempo di ogni pilota -- senza gamertag -- e un'immagine della schermata dei risultati come prova, visibile solo all'amministratore del sito."},
 "pt": {"hinweis": "Ativado, os resultados das corridas do Horizon Play também são enviados: carro, posição e tempo de cada piloto -- sem gamertags -- e uma imagem da tela de resultados como prova, que só o administrador do site vê."},
 "nl": {"hinweis": "Staat dit aan, dan gaan ook de uitslagen van Horizon Play-races mee: per coureur auto, plaats en tijd -- geen gamertags -- en een afbeelding van het uitslagenscherm als bewijs, die alleen de beheerder van de site ziet."},
 "pl": {"hinweis": "Gdy to jest włączone, wysyłane są też wyniki wyścigów Horizon Play: auto, miejsce i czas każdego kierowcy -- bez gamertagów -- oraz obraz ekranu wyników jako dowód, który widzi tylko administrator strony."},
 "cs": {"hinweis": "Je-li to zapnuté, odesílají se i výsledky závodů Horizon Play: auto, umístění a čas každého jezdce -- bez gamertagů -- a obrázek obrazovky výsledků jako důkaz, který vidí jen správce stránky."},
 "da": {"hinweis": "Når dette er slået til, sendes også resultaterne af Horizon Play-løb: hver kørers bil, placering og tid -- ingen gamertags -- og et billede af resultatskærmen som bevis, som kun sidens administrator ser."},
 "sv": {"hinweis": "När detta är på skickas även resultaten från Horizon Play-lopp: varje förares bil, placering och tid -- inga gamertags -- och en bild av resultatskärmen som bevis, som bara webbplatsens administratör ser."},
 "fi": {"hinweis": "Kun tämä on päällä, myös Horizon Play -kilpailujen tulokset lähetetään: jokaisen kuljettajan auto, sija ja aika -- ei gamertageja -- sekä kuva tulosnäytöstä todisteeksi, jonka näkee vain sivuston ylläpitäjä."},
 "hu": {"hinweis": "Ha be van kapcsolva, a Horizon Play versenyek eredményei is elküldésre kerülnek: minden versenyző autója, helyezése és ideje -- gamertagek nélkül -- és az eredményképernyő képe bizonyítékként, amelyet csak az oldal adminisztrátora lát."},
 "ro": {"hinweis": "Când este activat, se trimit și rezultatele curselor Horizon Play: mașina, locul și timpul fiecărui pilot -- fără gamertag-uri -- și o imagine a ecranului de rezultate ca dovadă, pe care o vede doar administratorul site-ului."},
 "el": {"hinweis": "Όταν είναι ενεργό, στέλνονται και τα αποτελέσματα των αγώνων Horizon Play: αυτοκίνητο, θέση και χρόνος κάθε οδηγού -- χωρίς gamertags -- και μια εικόνα της οθόνης αποτελεσμάτων ως απόδειξη, που τη βλέπει μόνο ο διαχειριστής του ιστότοπου."},
 "tr": {"hinweis": "Bu açıkken Horizon Play yarışlarının sonuçları da gönderilir: her sürücünün aracı, sırası ve süresi -- gamertag olmadan -- ve kanıt olarak, yalnızca sitenin yöneticisinin gördüğü bir sonuç ekranı görüntüsü."},
 "ru": {"hinweis": "Если это включено, отправляются и результаты гонок Horizon Play: машина, место и время каждого гонщика -- без геймертегов -- и снимок экрана результатов как доказательство, который видит только администратор сайта."},
 "ja": {"hinweis": "オンにすると、Horizon Play のレース結果も送信されます：各ドライバーの車、順位、タイム（ゲーマータグなし）と、証拠として結果画面の画像。画像はサイトの管理者だけが見ます。"},
 "ko": {"hinweis": "켜 두면 Horizon Play 레이스 결과도 전송됩니다: 각 드라이버의 차량, 순위, 시간(게이머태그 제외)과 증거로 결과 화면 이미지. 이미지는 사이트 관리자만 봅니다."},
 "zh-Hans": {"hinweis": "开启后，也会发送 Horizon Play 比赛结果：每位车手的车辆、名次和时间（不含玩家代号），以及一张结果画面截图作为证明，截图仅网站管理员可见。"},
 "zh-Hant": {"hinweis": "開啟後，也會傳送 Horizon Play 比賽結果：每位車手的車輛、名次和時間（不含玩家代號），以及一張結果畫面截圖作為證明，截圖僅網站管理員可見。"},
 "th": {"hinweis": "เมื่อเปิดไว้ ผลการแข่ง Horizon Play จะถูกส่งด้วย: รถ อันดับ และเวลาของนักแข่งแต่ละคน -- ไม่มีเกมเมอร์แท็ก -- และภาพหน้าจอผลการแข่งเป็นหลักฐาน ซึ่งมีเพียงผู้ดูแลเว็บไซต์ที่เห็น"},
 "vi": {"hinweis": "Khi bật, kết quả các cuộc đua Horizon Play cũng được gửi: xe, thứ hạng và thời gian của từng tay đua -- không có gamertag -- cùng ảnh màn hình kết quả làm bằng chứng, chỉ quản trị viên trang web xem được."},
 "id": {"hinweis": "Jika aktif, hasil balapan Horizon Play juga dikirim: mobil, posisi, dan waktu tiap pembalap -- tanpa gamertag -- serta gambar layar hasil sebagai bukti, yang hanya dilihat admin situs."},
 "ms": {"hinweis": "Jika dihidupkan, keputusan perlumbaan Horizon Play turut dihantar: kereta, kedudukan dan masa setiap pelumba -- tanpa gamertag -- serta gambar skrin keputusan sebagai bukti, yang hanya dilihat oleh pentadbir laman."},
 "nb": {"hinweis": "Når dette er på, sendes også resultatene fra Horizon Play-løp: hver førers bil, plassering og tid -- ingen gamertags -- og et bilde av resultatskjermen som bevis, som bare sidens administrator ser."},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
