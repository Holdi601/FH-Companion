# -*- coding: utf-8 -*-
"""Die Wertung im Reiter "My times" (2026-09-25), 25 Sprachen.

Die eigenen Autos gegeneinander, mit den Punkten der Auswertungsseite: Platz 1 auf
einem Kurs bekommt so viele Punkte, wie Autos dort gefahren sind. Die Platzhalter
{0}..{2} muessen in jeder Sprache stehen bleiben.

Nicht von Muttersprachlern geprueft.
"""

A = "Show my laps"
B = "Standings by points"
C = "Standings by total time"
D = "Points"
E = "Courses"
F = "Wins"
G = "Total time"
H = ("{0} car(s) in {1} class(es), scored over {2} course(s). On each course, place 1 "
     "earns as many points as cars you drove there, like on the website; standing and "
     "flying starts count separately. * = the total includes the slowest time on "
     "courses that car did not drive.")

# kennung: (A, B, C, D, E, F, G, H)
_UEBERSETZT = {
 "de": ("Meine Runden zeigen", "Wertung nach Punkten", "Wertung nach Gesamtzeit", "Punkte",
        "Kurse", "Siege", "Gesamtzeit",
        "{0} Auto(s) in {1} Klasse(n), gewertet über {2} Kurs(e). Auf jedem Kurs bekommt Platz 1 so viele Punkte, wie du dort Autos gefahren bist, wie auf der Website; stehende und fliegende Starts zählen getrennt. * = die Gesamtzeit enthält die langsamste Zeit auf Kursen, die dieses Auto nicht gefahren ist."),
 "fr": ("Afficher mes tours", "Classement aux points", "Classement au temps total", "Points",
        "Parcours", "Victoires", "Temps total",
        "{0} voiture(s) dans {1} classe(s), classées sur {2} parcours. Sur chaque parcours, la 1re place rapporte autant de points que de voitures que tu y as conduites, comme sur le site ; départs arrêtés et lancés comptent séparément. * = le total inclut le temps le plus lent sur les parcours que cette voiture n'a pas faits."),
 "es": ("Mostrar mis vueltas", "Clasificación por puntos", "Clasificación por tiempo total", "Puntos",
        "Circuitos", "Victorias", "Tiempo total",
        "{0} coche(s) en {1} clase(s), puntuados en {2} circuito(s). En cada circuito, el 1.º puesto gana tantos puntos como coches hayas conducido allí, como en la web; las salidas paradas y lanzadas cuentan por separado. * = el total incluye el tiempo más lento en los circuitos que ese coche no ha corrido."),
 "it": ("Mostra i miei giri", "Classifica a punti", "Classifica per tempo totale", "Punti",
        "Percorsi", "Vittorie", "Tempo totale",
        "{0} auto in {1} classe/i, valutate su {2} percorso/i. Su ogni percorso il 1° posto prende tanti punti quante auto hai guidato lì, come sul sito; partenze da fermo e lanciate contano a parte. * = il totale include il tempo più lento sui percorsi che quell'auto non ha fatto."),
 "pt": ("Mostrar as minhas voltas", "Classificação por pontos", "Classificação por tempo total", "Pontos",
        "Percursos", "Vitórias", "Tempo total",
        "{0} carro(s) em {1} classe(s), pontuados em {2} percurso(s). Em cada percurso, o 1.º lugar recebe tantos pontos quantos os carros que lá conduziste, como no site; partidas paradas e lançadas contam à parte. * = o total inclui o tempo mais lento nos percursos que esse carro não fez."),
 "nl": ("Mijn rondes tonen", "Stand op punten", "Stand op totale tijd", "Punten",
        "Parcoursen", "Overwinningen", "Totale tijd",
        "{0} auto('s) in {1} klasse(n), beoordeeld over {2} parcours(en). Op elk parcours krijgt plaats 1 evenveel punten als het aantal auto's dat je daar reed, zoals op de website; staande en vliegende starts tellen apart. * = het totaal bevat de langzaamste tijd op parcoursen die die auto niet reed."),
 "pl": ("Pokaż moje okrążenia", "Klasyfikacja punktowa", "Klasyfikacja wg łącznego czasu", "Punkty",
        "Trasy", "Wygrane", "Łączny czas",
        "{0} aut(o) w {1} klas(ach), ocenianych na {2} tras(ach). Na każdej trasie 1. miejsce dostaje tyle punktów, ile aut tam przejechałeś, jak na stronie; starty z miejsca i lotne liczą się osobno. * = łączny czas zawiera najwolniejszy czas na trasach, których to auto nie przejechało."),
 "sv": ("Visa mina varv", "Ställning på poäng", "Ställning på total tid", "Poäng",
        "Banor", "Segrar", "Total tid",
        "{0} bil(ar) i {1} klass(er), räknade över {2} bana/banor. På varje bana får plats 1 lika många poäng som antalet bilar du kört där, som på webbplatsen; stående och flygande starter räknas var för sig. * = totalen innehåller den långsammaste tiden på banor som bilen inte kört."),
 "da": ("Vis mine omgange", "Stilling efter point", "Stilling efter samlet tid", "Point",
        "Baner", "Sejre", "Samlet tid",
        "{0} bil(er) i {1} klasse(r), opgjort over {2} bane(r). På hver bane får 1. pladsen lige så mange point, som du har kørt biler der, som på hjemmesiden; stående og flyvende starter tæller hver for sig. * = den samlede tid indeholder den langsomste tid på baner, bilen ikke har kørt."),
 "fi": ("Näytä omat kierrokseni", "Pistetilanne", "Tilanne kokonaisajan mukaan", "Pisteet",
        "Radat", "Voitot", "Kokonaisaika",
        "{0} autoa {1} luokassa, pisteytetty {2} radalla. Kullakin radalla 1. sija saa yhtä monta pistettä kuin ajoit siellä autoja, kuten verkkosivulla; paikaltaan ja lentävästä lähdöt lasketaan erikseen. * = kokonaisaikaan sisältyy hitain aika radoilta, joita auto ei ajanut."),
 "cs": ("Zobrazit moje kola", "Pořadí podle bodů", "Pořadí podle celkového času", "Body",
        "Tratě", "Výhry", "Celkový čas",
        "{0} aut v {1} třídách, hodnoceno na {2} tratích. Na každé trati dostane 1. místo tolik bodů, kolik aut jsi tam odjel, stejně jako na webu; starty z místa a letmé se počítají zvlášť. * = celkový čas obsahuje nejpomalejší čas na tratích, které to auto nejelo."),
 "hu": ("Saját köreim", "Állás pontok szerint", "Állás összidő szerint", "Pontok",
        "Pályák", "Győzelmek", "Összidő",
        "{0} autó {1} osztályban, {2} pályán értékelve. Minden pályán az 1. hely annyi pontot kap, ahány autóval ott mentél, mint a weboldalon; az álló és a repülő rajt külön számít. * = az összidő tartalmazza a leglassabb időt azokon a pályákon, amelyeken az autó nem ment."),
 "ro": ("Arată turele mele", "Clasament după puncte", "Clasament după timp total", "Puncte",
        "Trasee", "Victorii", "Timp total",
        "{0} mașin(i) în {1} clas(e), punctate pe {2} trase(e). Pe fiecare traseu, locul 1 primește atâtea puncte câte mașini ai condus acolo, ca pe site; plecările de pe loc și lansate contează separat. * = totalul include cel mai lent timp de pe traseele pe care mașina nu le-a parcurs."),
 "el": ("Οι γύροι μου", "Κατάταξη με βαθμούς", "Κατάταξη με συνολικό χρόνο", "Βαθμοί",
        "Διαδρομές", "Νίκες", "Συνολικός χρόνος",
        "{0} αυτοκίνητα σε {1} κατηγορίες, βαθμολογημένα σε {2} διαδρομές. Σε κάθε διαδρομή η 1η θέση παίρνει τόσους βαθμούς όσα αυτοκίνητα οδήγησες εκεί, όπως στον ιστότοπο· οι στάσιμες και οι ιπτάμενες εκκινήσεις μετρούν χωριστά. * = ο συνολικός χρόνος περιλαμβάνει τον πιο αργό χρόνο στις διαδρομές που το αυτοκίνητο δεν οδήγησε."),
 "ru": ("Показать мои круги", "Зачёт по очкам", "Зачёт по общему времени", "Очки",
        "Трассы", "Победы", "Общее время",
        "{0} машин в {1} классах, зачёт по {2} трассам. На каждой трассе 1-е место получает столько очков, сколько машин ты там проехал, как на сайте; старт с места и с хода считаются отдельно. * = общее время включает самое медленное время на трассах, которые эта машина не проехала."),
 "tr": ("Turlarımı göster", "Puana göre sıralama", "Toplam süreye göre sıralama", "Puan",
        "Parkurlar", "Galibiyet", "Toplam süre",
        "{1} sınıfta {0} araç, {2} parkur üzerinden puanlandı. Her parkurda 1. sıra, orada sürdüğün araç sayısı kadar puan alır, web sitesindeki gibi; duran ve hareketli kalkışlar ayrı sayılır. * = toplam süre, o aracın sürmediği parkurlardaki en yavaş süreyi içerir."),
 "id": ("Tampilkan lap saya", "Klasemen poin", "Klasemen waktu total", "Poin",
        "Lintasan", "Menang", "Waktu total",
        "{0} mobil di {1} kelas, dinilai di {2} lintasan. Di setiap lintasan, posisi 1 mendapat poin sebanyak mobil yang kamu kendarai di sana, seperti di situs web; start berdiri dan melayang dihitung terpisah. * = total mencakup waktu paling lambat di lintasan yang tidak dikendarai mobil itu."),
 "ms": ("Tunjukkan pusingan saya", "Kedudukan mengikut mata", "Kedudukan mengikut masa keseluruhan", "Mata",
        "Laluan", "Kemenangan", "Masa keseluruhan",
        "{0} kereta dalam {1} kelas, dinilai atas {2} laluan. Di setiap laluan, tempat pertama mendapat mata sebanyak kereta yang anda pandu di situ, seperti di laman web; mula berdiri dan mula terbang dikira berasingan. * = jumlah termasuk masa paling perlahan di laluan yang tidak dipandu oleh kereta itu."),
 "vi": ("Hiện các vòng của tôi", "Bảng xếp hạng theo điểm", "Bảng xếp hạng theo tổng thời gian", "Điểm",
        "Đường đua", "Thắng", "Tổng thời gian",
        "{0} xe trong {1} hạng, tính điểm trên {2} đường đua. Trên mỗi đường đua, hạng 1 nhận số điểm bằng số xe bạn đã lái ở đó, giống như trên trang web; xuất phát đứng yên và xuất phát chạy đà được tính riêng. * = tổng thời gian gồm thời gian chậm nhất ở các đường đua mà xe đó chưa chạy."),
 "th": ("แสดงรอบของฉัน", "อันดับตามคะแนน", "อันดับตามเวลารวม", "คะแนน",
        "สนาม", "ชนะ", "เวลารวม",
        "รถ {0} คันใน {1} คลาส ให้คะแนนจาก {2} สนาม ในแต่ละสนาม อันดับ 1 ได้คะแนนเท่ากับจำนวนรถที่คุณขับที่นั่น เหมือนบนเว็บไซต์ การออกตัวแบบหยุดนิ่งและแบบวิ่งนับแยกกัน * = เวลารวมรวมเวลาที่ช้าที่สุดในสนามที่รถคันนั้นไม่ได้ขับ"),
 "ja": ("自分のラップを表示", "ポイント順位", "合計タイム順位", "ポイント",
        "コース", "勝利", "合計タイム",
        "{1} クラスの {0} 台を {2} コースで採点。各コースで 1 位は、そこで走らせた車の台数と同じポイントを得ます（ウェブサイトと同じ）。スタンディングとフライングのスタートは別々に数えます。* = 合計タイムには、その車が走っていないコースの最も遅いタイムが含まれます。"),
 "ko": ("내 랩 보기", "포인트 순위", "총 시간 순위", "포인트",
        "코스", "우승", "총 시간",
        "{1}개 클래스의 차량 {0}대를 {2}개 코스에서 채점했습니다. 각 코스에서 1위는 그곳에서 달린 차량 수만큼 포인트를 받습니다(웹사이트와 동일). 정지 출발과 주행 출발은 따로 계산합니다. * = 총 시간에는 그 차량이 달리지 않은 코스의 가장 느린 시간이 포함됩니다."),
 "zh-Hans": ("显示我的圈速", "按积分排名", "按总时间排名", "积分",
             "赛道", "胜场", "总时间",
             "{1} 个级别中的 {0} 辆车，在 {2} 条赛道上计分。每条赛道上第 1 名获得的积分等于你在该赛道驾驶过的车辆数，与网站相同；静止起步和行进起步分开计算。* = 总时间包含该车未跑过的赛道上的最慢时间。"),
 "zh-Hant": ("顯示我的圈速", "依積分排名", "依總時間排名", "積分",
             "賽道", "勝場", "總時間",
             "{1} 個級別中的 {0} 輛車，在 {2} 條賽道上計分。每條賽道上第 1 名獲得的積分等於你在該賽道駕駛過的車輛數，與網站相同；靜止起步和行進起步分開計算。* = 總時間包含該車未跑過的賽道上的最慢時間。"),
}

_KEYS = (A, B, C, D, E, F, G, H)
for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(_KEYS), (_code, len(_saetze))
    for _ph in ("{0}", "{1}", "{2}"):
        assert _ph in _saetze[7], (_code, _ph)

ZUSATZ = {code: dict(zip(_KEYS, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
