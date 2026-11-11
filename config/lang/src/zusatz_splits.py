# -*- coding: utf-8 -*-
"""Split-Tendenz neben der Positionsliste (2026-11-11). 25 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "switch": "Split trend beside the race positions",
    "note": "A column left of the game's position list: how many seconds each driver is ahead of you (-) or behind you (+), green when that gap moved in your favour since the last split, red when it moved against you, grey when it stayed. It reads the list twice a second and disappears with it.",
}

_UEBERSETZT = {
 "de": {"switch": "Split-Tendenz neben der Positionsliste",
        "note": "Eine Spalte links neben der Positionsliste des Spiels: wie viele Sekunden jeder Fahrer vor dir (-) oder hinter dir (+) liegt, grün, wenn sich der Abstand seit dem letzten Split zu deinen Gunsten bewegt hat, rot, wenn gegen dich, grau, wenn er gleich blieb. Sie liest die Liste zweimal pro Sekunde und verschwindet mit ihr."},
 "fr": {"switch": "Tendance des temps intermédiaires à côté du classement",
        "note": "Une colonne à gauche du classement du jeu : combien de secondes chaque pilote a d'avance (-) ou de retard (+) sur vous, en vert si l'écart a évolué en votre faveur depuis le dernier intermédiaire, en rouge s'il a évolué contre vous, en gris s'il n'a pas changé. Elle lit la liste deux fois par seconde et disparaît avec elle."},
 "es": {"switch": "Tendencia de parciales junto a las posiciones",
        "note": "Una columna a la izquierda de la lista de posiciones del juego: cuántos segundos va cada piloto por delante (-) o por detrás (+) de ti, en verde si la diferencia cambió a tu favor desde el último parcial, en rojo si cambió en tu contra, en gris si siguió igual. Lee la lista dos veces por segundo y desaparece con ella."},
 "it": {"switch": "Tendenza degli intermedi accanto alle posizioni",
        "note": "Una colonna a sinistra della lista posizioni del gioco: quanti secondi ogni pilota è davanti (-) o dietro (+) a te, verde se il distacco è cambiato a tuo favore dall'ultimo intermedio, rosso se è cambiato contro di te, grigio se è rimasto uguale. Legge la lista due volte al secondo e scompare con essa."},
 "pt": {"switch": "Tendência dos parciais junto às posições",
        "note": "Uma coluna à esquerda da lista de posições do jogo: quantos segundos cada piloto está à tua frente (-) ou atrás (+), verde se a diferença mudou a teu favor desde o último parcial, vermelho se mudou contra ti, cinzento se ficou igual. Lê a lista duas vezes por segundo e desaparece com ela."},
 "nl": {"switch": "Split-trend naast de racestand",
        "note": "Een kolom links van de standenlijst van de game: hoeveel seconden elke rijder voor (-) of achter (+) je ligt, groen als het verschil sinds de laatste split in jouw voordeel veranderde, rood als het tegen je veranderde, grijs als het gelijk bleef. Ze leest de lijst twee keer per seconde en verdwijnt samen met die lijst."},
 "pl": {"switch": "Trend międzyczasów obok pozycji",
        "note": "Kolumna na lewo od listy pozycji w grze: ile sekund każdy kierowca jest przed tobą (-) lub za tobą (+), zielona, gdy różnica od ostatniego międzyczasu zmieniła się na twoją korzyść, czerwona, gdy na twoją niekorzyść, szara, gdy się nie zmieniła. Czyta listę dwa razy na sekundę i znika razem z nią."},
 "cs": {"switch": "Trend mezičasů vedle pořadí",
        "note": "Sloupec vlevo od seznamu pořadí ve hře: kolik sekund je každý jezdec před tebou (-) nebo za tebou (+), zeleně, když se odstup od posledního mezičasu změnil ve tvůj prospěch, červeně, když v tvůj neprospěch, šedě, když zůstal stejný. Čte seznam dvakrát za sekundu a mizí spolu s ním."},
 "da": {"switch": "Split-tendens ved siden af placeringerne",
        "note": "En kolonne til venstre for spillets placeringsliste: hvor mange sekunder hver kører er foran (-) eller bag (+) dig, grøn når afstanden siden sidste split ændrede sig til din fordel, rød når den ændrede sig imod dig, grå når den var uændret. Den læser listen to gange i sekundet og forsvinder sammen med den."},
 "sv": {"switch": "Splittrend bredvid placeringarna",
        "note": "En kolumn till vänster om spelets placeringslista: hur många sekunder varje förare ligger före (-) eller efter (+) dig, grön när avståndet sedan senaste splitten ändrades till din fördel, röd när det ändrades emot dig, grå när det var oförändrat. Den läser listan två gånger i sekunden och försvinner tillsammans med den."},
 "nb": {"switch": "Split-trend ved siden av plasseringene",
        "note": "En kolonne til venstre for spillets plasseringsliste: hvor mange sekunder hver fører ligger foran (-) eller bak (+) deg, grønn når avstanden siden forrige split endret seg til din fordel, rød når den endret seg mot deg, grå når den var uendret. Den leser listen to ganger i sekundet og forsvinner sammen med den."},
 "fi": {"switch": "Väliaikatrendi sijoitusten vieressä",
        "note": "Sarake pelin sijoituslistan vasemmalla puolella: montako sekuntia kukin kuljettaja on edelläsi (-) tai takanasi (+), vihreä kun ero muuttui edellisen väliajan jälkeen sinun eduksesi, punainen kun se muuttui sinua vastaan, harmaa kun se pysyi samana. Se lukee listan kahdesti sekunnissa ja katoaa sen mukana."},
 "hu": {"switch": "Részidő-tendencia a helyezések mellett",
        "note": "Egy oszlop a játék helyezéslistájától balra: hány másodperccel jár előtted (-) vagy mögötted (+) minden versenyző, zöld, ha a különbség az előző részidő óta a javadra változott, piros, ha ellened, szürke, ha nem változott. Másodpercenként kétszer olvassa a listát, és vele együtt tűnik el."},
 "ro": {"switch": "Tendința timpilor intermediari lângă poziții",
        "note": "O coloană în stânga listei de poziții a jocului: câte secunde este fiecare pilot în fața ta (-) sau în urma ta (+), verde când diferența s-a schimbat în favoarea ta de la ultimul intermediar, roșu când s-a schimbat împotriva ta, gri când a rămas la fel. Citește lista de două ori pe secundă și dispare odată cu ea."},
 "el": {"switch": "Τάση ενδιάμεσων χρόνων δίπλα στις θέσεις",
        "note": "Μια στήλη αριστερά από τη λίστα θέσεων του παιχνιδιού: πόσα δευτερόλεπτα είναι κάθε οδηγός μπροστά (-) ή πίσω (+) από σένα, πράσινη όταν η διαφορά άλλαξε υπέρ σου από τον τελευταίο ενδιάμεσο, κόκκινη όταν άλλαξε εις βάρος σου, γκρι όταν έμεινε ίδια. Διαβάζει τη λίστα δύο φορές το δευτερόλεπτο και εξαφανίζεται μαζί της."},
 "tr": {"switch": "Sıralamanın yanında ara zaman eğilimi",
        "note": "Oyunun sıralama listesinin solunda bir sütun: her sürücünün senden kaç saniye önde (-) veya geride (+) olduğu; son ara zamandan beri fark senin lehine değiştiyse yeşil, aleyhine değiştiyse kırmızı, aynı kaldıysa gri. Listeyi saniyede iki kez okur ve onunla birlikte kaybolur."},
 "ru": {"switch": "Тренд отсечек рядом с позициями",
        "note": "Столбец слева от списка позиций в игре: на сколько секунд каждый гонщик впереди (-) или позади (+) тебя, зелёный, если разрыв с прошлой отсечки изменился в твою пользу, красный, если против тебя, серый, если не изменился. Он читает список дважды в секунду и исчезает вместе с ним."},
 "ja": {"switch": "順位表の横にスプリットの傾向",
        "note": "ゲームの順位表の左に表示する列です。各ドライバーがあなたより何秒前（-）か後ろ（+）かを示し、前回のスプリットから差があなたに有利に動けば緑、不利に動けば赤、変わらなければ灰色になります。1 秒に 2 回順位表を読み取り、順位表と一緒に消えます。"},
 "ko": {"switch": "순위 옆에 스플릿 추세",
        "note": "게임 순위 목록 왼쪽에 표시되는 열입니다. 각 드라이버가 나보다 몇 초 앞(-) 또는 뒤(+)에 있는지 보여 주며, 지난 스플릿 이후 격차가 나에게 유리하게 바뀌면 초록, 불리하게 바뀌면 빨강, 그대로면 회색입니다. 목록을 1초에 두 번 읽고 목록과 함께 사라집니다."},
 "zh-Hans": {"switch": "在名次旁显示分段趋势",
             "note": "游戏名次列表左侧的一列：每位车手领先你（-）或落后你（+）多少秒；自上一个分段以来差距朝有利于你的方向变化时为绿色，不利时为红色，不变时为灰色。它每秒读取两次名次列表，并随列表一起消失。"},
 "zh-Hant": {"switch": "在名次旁顯示分段趨勢",
             "note": "遊戲名次列表左側的一欄：每位車手領先你（-）或落後你（+）多少秒；自上一個分段以來差距朝有利於你的方向變化時為綠色，不利時為紅色，不變時為灰色。它每秒讀取兩次名次列表，並隨列表一起消失。"},
 "id": {"switch": "Tren split di samping posisi balapan",
        "note": "Kolom di kiri daftar posisi game: berapa detik setiap pembalap di depan (-) atau di belakang (+) kamu, hijau bila selisihnya sejak split terakhir berubah menguntungkanmu, merah bila merugikanmu, abu-abu bila tetap. Kolom ini membaca daftar dua kali per detik dan hilang bersama daftar itu."},
 "ms": {"switch": "Trend split di sebelah kedudukan perlumbaan",
        "note": "Satu lajur di kiri senarai kedudukan permainan: berapa saat setiap pemandu di hadapan (-) atau di belakang (+) anda, hijau apabila jarak sejak split terakhir berubah memihak anda, merah apabila berubah menentang anda, kelabu apabila kekal. Ia membaca senarai dua kali sesaat dan hilang bersama senarai itu."},
 "th": {"switch": "แนวโน้มสปลิตข้างอันดับการแข่ง",
        "note": "คอลัมน์ทางซ้ายของรายการอันดับในเกม: นักแข่งแต่ละคนนำหน้า (-) หรือตามหลัง (+) คุณกี่วินาที เป็นสีเขียวเมื่อระยะห่างเปลี่ยนไปในทางที่ดีต่อคุณนับจากสปลิตล่าสุด สีแดงเมื่อเปลี่ยนไปในทางที่เสียเปรียบ สีเทาเมื่อเท่าเดิม อ่านรายการวินาทีละสองครั้งและหายไปพร้อมกับรายการ"},
 "vi": {"switch": "Xu hướng split bên cạnh thứ hạng",
        "note": "Một cột bên trái danh sách thứ hạng của trò chơi: mỗi tay đua đang dẫn trước (-) hay ở sau (+) bạn bao nhiêu giây, màu xanh khi khoảng cách từ split trước thay đổi có lợi cho bạn, màu đỏ khi bất lợi, màu xám khi không đổi. Cột này đọc danh sách hai lần mỗi giây và biến mất cùng danh sách."},
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
