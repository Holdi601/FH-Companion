# -*- coding: utf-8 -*-
"""Streckenlisten aus dem Spiel lernen, mehrere Boards erneut senden (2026-11-11). 25 Sprachen, nicht
von Muttersprachlern geprueft. "Waiting for manual review" ist der Text der Website und bleibt englisch."""

K = {
    "again": "Send these {0} boards again?\n\nA board sent again is not put on the website on its own: an admin looks at it first. The website shows each as \"Waiting for manual review\" until then.",
    "learning": "Learning the route list of {0} from the game ...",
    "button": "Learn route lists",
}

_UEBERSETZT = {
 "de": {"again": "Diese {0} Boards erneut senden?\n\nEin erneut gesendetes Board kommt nicht von selbst auf die Website: Erst sieht es sich ein Admin an. Bis dahin zeigt die Website jedes als „Waiting for manual review“.",
        "learning": "Lerne die Streckenliste von {0} aus dem Spiel ...", "button": "Streckenlisten lernen"},
 "fr": {"again": "Renvoyer ces {0} classements ?\n\nUn classement renvoyé n'arrive pas seul sur le site : un admin le regarde d'abord. Le site affiche chacun comme « Waiting for manual review » jusque-là.",
        "learning": "Apprentissage de la liste des parcours de {0} depuis le jeu ...", "button": "Apprendre les listes de parcours"},
 "es": {"again": "¿Volver a enviar estas {0} clasificaciones?\n\nUna clasificación reenviada no llega sola a la web: primero la revisa un admin. Hasta entonces la web muestra cada una como «Waiting for manual review».",
        "learning": "Aprendiendo la lista de rutas de {0} desde el juego ...", "button": "Aprender listas de rutas"},
 "it": {"again": "Inviare di nuovo queste {0} classifiche?\n\nUna classifica reinviata non arriva da sola sul sito: prima la guarda un admin. Fino ad allora il sito mostra ciascuna come «Waiting for manual review».",
        "learning": "Apprendo l'elenco dei percorsi di {0} dal gioco ...", "button": "Apprendi elenchi percorsi"},
 "pt": {"again": "Enviar de novo estas {0} tabelas?\n\nUma tabela reenviada não chega sozinha ao site: primeiro um admin vê-a. Até lá o site mostra cada uma como «Waiting for manual review».",
        "learning": "A aprender a lista de percursos de {0} a partir do jogo ...", "button": "Aprender listas de percursos"},
 "nl": {"again": "Deze {0} klassementen opnieuw versturen?\n\nEen opnieuw verstuurd klassement komt niet vanzelf op de website: eerst kijkt een admin ernaar. Tot dan toont de website elk als ‘Waiting for manual review’.",
        "learning": "De routelijst van {0} uit de game leren ...", "button": "Routelijsten leren"},
 "pl": {"again": "Wysłać ponownie te {0} tablice?\n\nPonownie wysłana tablica nie trafia sama na stronę: najpierw ogląda ją admin. Do tego czasu strona pokazuje każdą jako „Waiting for manual review”.",
        "learning": "Uczę się listy tras {0} z gry ...", "button": "Naucz listy tras"},
 "cs": {"again": "Odeslat znovu těchto {0} žebříčků?\n\nZnovu odeslaný žebříček se na web nedostane sám: nejdřív ho zkontroluje admin. Do té doby ho web ukazuje jako „Waiting for manual review“.",
        "learning": "Učím se seznam tras {0} ze hry ...", "button": "Naučit seznamy tras"},
 "da": {"again": "Send disse {0} ranglister igen?\n\nEn rangliste, der sendes igen, kommer ikke selv på hjemmesiden: en admin ser på den først. Indtil da viser siden hver som \"Waiting for manual review\".",
        "learning": "Lærer rutelisten for {0} fra spillet ...", "button": "Lær rutelister"},
 "sv": {"again": "Skicka dessa {0} topplistor igen?\n\nEn topplista som skickas igen hamnar inte på webbplatsen av sig själv: en admin tittar först. Tills dess visar webbplatsen var och en som ”Waiting for manual review”.",
        "learning": "Lär in ruttlistan för {0} från spelet ...", "button": "Lär in ruttlistor"},
 "nb": {"again": "Send disse {0} topplistene på nytt?\n\nEn toppliste som sendes på nytt, kommer ikke av seg selv på nettsiden: en admin ser på den først. Til da viser nettsiden hver som «Waiting for manual review».",
        "learning": "Lærer rutelisten for {0} fra spillet ...", "button": "Lær rutelister"},
 "fi": {"again": "Lähetetäänkö nämä {0} tulostaulua uudelleen?\n\nUudelleen lähetetty tulostaulu ei mene sivustolle itsestään: ylläpitäjä katsoo sen ensin. Siihen asti sivusto näyttää jokaisen tilana ”Waiting for manual review”.",
        "learning": "Opetellaan kategorian {0} reittilista pelistä ...", "button": "Opettele reittilistat"},
 "hu": {"again": "Újra elküldöd ezt a(z) {0} ranglistát?\n\nAz újraküldött ranglista nem kerül magától a weboldalra: előbb egy admin megnézi. Addig az oldal mindegyiket „Waiting for manual review” állapotban mutatja.",
        "learning": "{0} útvonallistájának megtanulása a játékból ...", "button": "Útvonallisták megtanulása"},
 "ro": {"again": "Trimiți din nou aceste {0} clasamente?\n\nUn clasament retrimis nu ajunge singur pe site: mai întâi îl verifică un admin. Până atunci site-ul îl arată pe fiecare ca „Waiting for manual review”.",
        "learning": "Învăț lista de trasee {0} din joc ...", "button": "Învață listele de trasee"},
 "el": {"again": "Να σταλούν ξανά αυτοί οι {0} πίνακες;\n\nΈνας πίνακας που στέλνεται ξανά δεν μπαίνει μόνος του στον ιστότοπο: πρώτα τον ελέγχει ένας διαχειριστής. Μέχρι τότε ο ιστότοπος δείχνει τον καθένα ως «Waiting for manual review».",
        "learning": "Εκμάθηση της λίστας διαδρομών {0} από το παιχνίδι ...", "button": "Εκμάθηση λιστών διαδρομών"},
 "tr": {"again": "Bu {0} tablo yeniden gönderilsin mi?\n\nYeniden gönderilen bir tablo siteye kendiliğinden girmez: önce bir yönetici bakar. O zamana kadar site her birini \"Waiting for manual review\" olarak gösterir.",
        "learning": "{0} rota listesi oyundan öğreniliyor ...", "button": "Rota listelerini öğren"},
 "ru": {"again": "Отправить эти таблицы ({0}) ещё раз?\n\nПовторно отправленная таблица не попадает на сайт сама: сначала её смотрит администратор. До тех пор сайт показывает каждую как «Waiting for manual review».",
        "learning": "Изучаю список трасс {0} из игры ...", "button": "Изучить списки трасс"},
 "ja": {"again": "これら {0} 件のボードを再送信しますか？\n\n再送信したボードはそのままサイトには載りません。まず管理者が確認します。それまでサイトでは各ボードが「Waiting for manual review」と表示されます。",
        "learning": "{0} のルート一覧をゲームから学習中 ...", "button": "ルート一覧を学習"},
 "ko": {"again": "이 보드 {0}개를 다시 보낼까요?\n\n다시 보낸 보드는 저절로 웹사이트에 올라가지 않습니다. 먼저 관리자가 확인합니다. 그때까지 웹사이트에는 각각 \"Waiting for manual review\"로 표시됩니다.",
        "learning": "게임에서 {0} 경로 목록을 학습하는 중 ...", "button": "경로 목록 학습"},
 "zh-Hans": {"again": "要重新发送这 {0} 个榜单吗？\n\n重新发送的榜单不会自动上网站：先由管理员查看。在此之前，网站会把每个都显示为“Waiting for manual review”。",
             "learning": "正在从游戏中学习 {0} 的路线列表 ...", "button": "学习路线列表"},
 "zh-Hant": {"again": "要重新傳送這 {0} 個排行榜嗎？\n\n重新傳送的排行榜不會自動上網站：先由管理員查看。在此之前，網站會把每個都顯示為「Waiting for manual review」。",
             "learning": "正在從遊戲中學習 {0} 的路線列表 ...", "button": "學習路線列表"},
 "id": {"again": "Kirim ulang {0} papan ini?\n\nPapan yang dikirim ulang tidak otomatis masuk ke situs: admin memeriksanya dulu. Sampai saat itu situs menampilkan masing-masing sebagai \"Waiting for manual review\".",
        "learning": "Mempelajari daftar rute {0} dari game ...", "button": "Pelajari daftar rute"},
 "ms": {"again": "Hantar semula {0} papan ini?\n\nPapan yang dihantar semula tidak masuk ke laman web dengan sendiri: admin menyemaknya dahulu. Sehingga itu laman web menunjukkan setiap satu sebagai \"Waiting for manual review\".",
        "learning": "Mempelajari senarai laluan {0} daripada permainan ...", "button": "Pelajari senarai laluan"},
 "th": {"again": "ส่งบอร์ดทั้ง {0} นี้อีกครั้งไหม?\n\nบอร์ดที่ส่งซ้ำจะไม่ขึ้นเว็บไซต์เอง: ผู้ดูแลจะตรวจก่อน จนกว่าจะถึงตอนนั้นเว็บไซต์จะแสดงแต่ละบอร์ดเป็น \"Waiting for manual review\"",
        "learning": "กำลังเรียนรู้รายการเส้นทางของ {0} จากเกม ...", "button": "เรียนรู้รายการเส้นทาง"},
 "vi": {"again": "Gửi lại {0} bảng này?\n\nBảng gửi lại không tự lên trang web: quản trị viên xem trước. Cho đến lúc đó trang web hiển thị mỗi bảng là \"Waiting for manual review\".",
        "learning": "Đang học danh sách tuyến của {0} từ trò chơi ...", "button": "Học danh sách tuyến"},
}

import re as _re
_FELD = _re.compile(r"\{\d+(?::[^}]*)?\}")
for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _en in K.items():
        assert sorted(_FELD.findall(_en)) == sorted(_FELD.findall(_saetze[_id])), (_code, _id)
        assert _saetze[_id].strip(), (_code, _id)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
