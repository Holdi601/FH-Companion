# -*- coding: utf-8 -*-
"""Der Rundenbestand im Reiter "My times": Ordner oeffnen, langsamere Runden loeschen (2026-10-01). 25 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "ordner": "Open lap folder",
    "knopf": "Delete slower laps…",
    "titel": "Delete slower laps",
    "nichts": "Nothing to delete: every lap is already the fastest of its car, course and class.",
    "frage": "{0} slower laps and {1} unfinished runs will be deleted, {2} in total.\n\nKept: your fastest lap per course, PI class and car -- standing and flying starts and each game mode on their own, as your records count them. Race statistics stay complete.\n\nThis cannot be undone.",
    "fertig": "Deleted: {0} laps. Freed: {1}.",
    "fehler": "{0} file(s) could not be deleted -- another program may have them open. Try again later.",
    "hinweis": 'Every lap is saved, with its full telemetry. The tab "{0}" opens their folder and deletes slower laps when you ask.',
}

_UEBERSETZT = {
 "de": {
  "ordner": "Rundenordner öffnen", "knopf": "Langsamere Runden löschen…", "titel": "Langsamere Runden löschen",
  "nichts": "Nichts zu löschen: Jede Runde ist schon die schnellste ihres Autos, Kurses und ihrer Klasse.",
  "frage": "{0} langsamere Runden und {1} abgebrochene Läufe werden gelöscht, zusammen {2}.\n\nBehalten wird deine schnellste Runde je Kurs, PI-Klasse und Auto -- stehender und fliegender Start und jeder Spielmodus für sich, so wie deine Rekorde zählen. Die Rennstatistik bleibt vollständig.\n\nDas lässt sich nicht rückgängig machen.",
  "fertig": "Gelöscht: {0} Runden. Frei: {1}.",
  "fehler": "{0} Datei(en) ließen sich nicht löschen -- ein anderes Programm hat sie vielleicht offen. Später noch einmal versuchen.",
  "hinweis": "Jede Runde wird gespeichert, mit ihrer vollen Telemetrie. Der Reiter „{0}“ öffnet ihren Ordner und löscht auf Wunsch langsamere Runden."},
 "fr": {
  "ordner": "Ouvrir le dossier des tours", "knopf": "Supprimer les tours plus lents…", "titel": "Supprimer les tours plus lents",
  "nichts": "Rien à supprimer : chaque tour est déjà le plus rapide de sa voiture, de son parcours et de sa classe.",
  "frage": "{0} tours plus lents et {1} courses inachevées seront supprimés, {2} au total.\n\nConservé : votre tour le plus rapide par parcours, classe PI et voiture -- départs arrêtés et lancés et chaque mode de jeu à part, comme vos records les comptent. Les statistiques de course restent complètes.\n\nCette action est irréversible.",
  "fertig": "Supprimés : {0} tours. Libéré : {1}.",
  "fehler": "{0} fichier(s) n'ont pas pu être supprimés -- un autre programme les a peut-être ouverts. Réessayez plus tard.",
  "hinweis": "Chaque tour est enregistré, avec sa télémétrie complète. L'onglet « {0} » ouvre leur dossier et supprime les tours plus lents à la demande."},
 "es": {
  "ordner": "Abrir carpeta de vueltas", "knopf": "Borrar vueltas más lentas…", "titel": "Borrar vueltas más lentas",
  "nichts": "Nada que borrar: cada vuelta ya es la más rápida de su coche, recorrido y clase.",
  "frage": "Se borrarán {0} vueltas más lentas y {1} carreras sin terminar, {2} en total.\n\nSe conserva tu vuelta más rápida por recorrido, clase de PI y coche -- salida parada y lanzada y cada modo de juego por separado, como cuentan tus récords. Las estadísticas de carreras quedan completas.\n\nEsto no se puede deshacer.",
  "fertig": "Borradas: {0} vueltas. Liberado: {1}.",
  "fehler": "No se pudieron borrar {0} archivo(s) -- quizá otro programa los tiene abiertos. Inténtalo más tarde.",
  "hinweis": "Cada vuelta se guarda con su telemetría completa. La pestaña «{0}» abre su carpeta y borra las vueltas más lentas cuando lo pidas."},
 "it": {
  "ordner": "Apri cartella dei giri", "knopf": "Elimina giri più lenti…", "titel": "Elimina giri più lenti",
  "nichts": "Niente da eliminare: ogni giro è già il più veloce della sua auto, del suo percorso e della sua classe.",
  "frage": "Verranno eliminati {0} giri più lenti e {1} corse non concluse, {2} in tutto.\n\nResta il tuo giro più veloce per percorso, classe PI e auto -- partenza da fermo e lanciata e ogni modalità di gioco a parte, come contano i tuoi record. Le statistiche delle gare restano complete.\n\nL'operazione non si può annullare.",
  "fertig": "Eliminati: {0} giri. Liberati: {1}.",
  "fehler": "Non è stato possibile eliminare {0} file -- forse un altro programma li tiene aperti. Riprova più tardi.",
  "hinweis": "Ogni giro viene salvato con la sua telemetria completa. La scheda «{0}» apre la loro cartella ed elimina i giri più lenti su richiesta."},
 "pt": {
  "ordner": "Abrir pasta das voltas", "knopf": "Excluir voltas mais lentas…", "titel": "Excluir voltas mais lentas",
  "nichts": "Nada para excluir: cada volta já é a mais rápida do seu carro, percurso e classe.",
  "frage": "{0} voltas mais lentas e {1} corridas não terminadas serão excluídas, {2} no total.\n\nFica a sua volta mais rápida por percurso, classe de PI e carro -- largada parada e lançada e cada modo de jogo à parte, como seus recordes contam. As estatísticas de corrida continuam completas.\n\nIsso não pode ser desfeito.",
  "fertig": "Excluídas: {0} voltas. Liberado: {1}.",
  "fehler": "{0} arquivo(s) não puderam ser excluídos -- talvez outro programa esteja com eles abertos. Tente mais tarde.",
  "hinweis": "Cada volta é salva com sua telemetria completa. A aba \"{0}\" abre a pasta delas e exclui voltas mais lentas quando você pedir."},
 "nl": {
  "ordner": "Rondemap openen", "knopf": "Langzamere ronden verwijderen…", "titel": "Langzamere ronden verwijderen",
  "nichts": "Niets te verwijderen: elke ronde is al de snelste van haar auto, parcours en klasse.",
  "frage": "{0} langzamere ronden en {1} afgebroken runs worden verwijderd, samen {2}.\n\nBewaard blijft je snelste ronde per parcours, PI-klasse en auto -- staande en vliegende start en elke spelmodus apart, zoals je records tellen. De racestatistieken blijven compleet.\n\nDit kan niet ongedaan worden gemaakt.",
  "fertig": "Verwijderd: {0} ronden. Vrijgemaakt: {1}.",
  "fehler": "{0} bestand(en) konden niet worden verwijderd -- misschien heeft een ander programma ze open. Probeer het later opnieuw.",
  "hinweis": "Elke ronde wordt opgeslagen, met haar volledige telemetrie. Het tabblad '{0}' opent hun map en verwijdert op verzoek langzamere ronden."},
 "pl": {
  "ordner": "Otwórz folder okrążeń", "knopf": "Usuń wolniejsze okrążenia…", "titel": "Usuń wolniejsze okrążenia",
  "nichts": "Nie ma nic do usunięcia: każde okrążenie jest już najszybsze dla swojego auta, trasy i klasy.",
  "frage": "Zostanie usuniętych {0} wolniejszych okrążeń i {1} nieukończonych przejazdów, razem {2}.\n\nZostaje twoje najszybsze okrążenie na trasę, klasę PI i auto -- start zatrzymany i lotny oraz każdy tryb gry osobno, tak jak liczą je twoje rekordy. Statystyki wyścigów pozostają kompletne.\n\nTego nie można cofnąć.",
  "fertig": "Usunięto: {0} okrążeń. Zwolniono: {1}.",
  "fehler": "Nie udało się usunąć {0} plik(ów) -- być może inny program ma je otwarte. Spróbuj później.",
  "hinweis": "Każde okrążenie jest zapisywane z pełną telemetrią. Karta „{0}” otwiera ich folder i na życzenie usuwa wolniejsze okrążenia."},
 "cs": {
  "ordner": "Otevřít složku kol", "knopf": "Smazat pomalejší kola…", "titel": "Smazat pomalejší kola",
  "nichts": "Není co mazat: každé kolo je už nejrychlejší pro své auto, trať a třídu.",
  "frage": "Smaže se {0} pomalejších kol a {1} nedokončených jízd, celkem {2}.\n\nZůstane tvé nejrychlejší kolo pro každou trať, třídu PI a auto -- start z místa i letmý start a každý herní režim zvlášť, tak jak je počítají tvé rekordy. Statistiky závodů zůstanou úplné.\n\nToto nelze vrátit zpět.",
  "fertig": "Smazáno: {0} kol. Uvolněno: {1}.",
  "fehler": "{0} soubor(ů) nešlo smazat -- možná je má otevřené jiný program. Zkus to později.",
  "hinweis": "Každé kolo se ukládá i s úplnou telemetrií. Karta „{0}“ otevře jejich složku a na požádání smaže pomalejší kola."},
 "da": {
  "ordner": "Åbn omgangsmappe", "knopf": "Slet langsommere omgange…", "titel": "Slet langsommere omgange",
  "nichts": "Intet at slette: hver omgang er allerede den hurtigste for sin bil, bane og klasse.",
  "frage": "{0} langsommere omgange og {1} afbrudte løb bliver slettet, {2} i alt.\n\nTilbage bliver din hurtigste omgang pr. bane, PI-klasse og bil -- stående og flyvende start og hver spiltilstand for sig, sådan som dine rekorder tæller. Løbsstatistikken forbliver komplet.\n\nDet kan ikke fortrydes.",
  "fertig": "Slettet: {0} omgange. Frigjort: {1}.",
  "fehler": "{0} fil(er) kunne ikke slettes -- et andet program har dem måske åbne. Prøv igen senere.",
  "hinweis": "Hver omgang gemmes med sin fulde telemetri. Fanen \"{0}\" åbner deres mappe og sletter langsommere omgange, når du beder om det."},
 "sv": {
  "ordner": "Öppna varvmappen", "knopf": "Radera långsammare varv…", "titel": "Radera långsammare varv",
  "nichts": "Inget att radera: varje varv är redan det snabbaste för sin bil, bana och klass.",
  "frage": "{0} långsammare varv och {1} avbrutna lopp raderas, {2} totalt.\n\nKvar blir ditt snabbaste varv per bana, PI-klass och bil -- stående och flygande start och varje spelläge för sig, så som dina rekord räknas. Loppstatistiken förblir komplett.\n\nDetta kan inte ångras.",
  "fertig": "Raderat: {0} varv. Frigjort: {1}.",
  "fehler": "{0} fil(er) kunde inte raderas -- ett annat program kanske har dem öppna. Försök igen senare.",
  "hinweis": "Varje varv sparas med sin fullständiga telemetri. Fliken \"{0}\" öppnar deras mapp och raderar långsammare varv när du vill."},
 "fi": {
  "ordner": "Avaa kierroskansio", "knopf": "Poista hitaammat kierrokset…", "titel": "Poista hitaammat kierrokset",
  "nichts": "Ei poistettavaa: jokainen kierros on jo autonsa, ratansa ja luokkansa nopein.",
  "frage": "{0} hitaampaa kierrosta ja {1} keskeytettyä ajoa poistetaan, yhteensä {2}.\n\nJäljelle jää nopein kierroksesi kullekin radalle, PI-luokalle ja autolle -- seisova ja lentävä lähtö sekä jokainen pelitila erikseen, kuten ennätyksesi ne laskevat. Kilpailutilastot säilyvät täydellisinä.\n\nTätä ei voi perua.",
  "fertig": "Poistettu: {0} kierrosta. Vapautettu: {1}.",
  "fehler": "{0} tiedostoa ei voitu poistaa -- jokin toinen ohjelma saattaa pitää niitä auki. Yritä myöhemmin uudelleen.",
  "hinweis": "Jokainen kierros tallennetaan täyden telemetrian kanssa. Välilehti \"{0}\" avaa niiden kansion ja poistaa pyynnöstä hitaammat kierrokset."},
 "hu": {
  "ordner": "Körmappa megnyitása", "knopf": "Lassabb körök törlése…", "titel": "Lassabb körök törlése",
  "nichts": "Nincs mit törölni: minden kör már a leggyorsabb a saját autójával, pályáján és kategóriájában.",
  "frage": "{0} lassabb kör és {1} félbehagyott futam törlődik, összesen {2}.\n\nMegmarad a leggyorsabb köröd pályánként, PI-kategóriánként és autónként -- álló és repülő rajt, valamint minden játékmód külön, ahogy a rekordjaid számolják. A versenystatisztika teljes marad.\n\nEz nem vonható vissza.",
  "fertig": "Törölve: {0} kör. Felszabadult: {1}.",
  "fehler": "{0} fájlt nem sikerült törölni -- talán egy másik program nyitva tartja. Próbáld újra később.",
  "hinweis": "Minden kör mentésre kerül a teljes telemetriával. A(z) „{0}” lap megnyitja a mappájukat, és kérésre törli a lassabb köröket."},
 "ro": {
  "ordner": "Deschide dosarul turelor", "knopf": "Șterge turele mai lente…", "titel": "Șterge turele mai lente",
  "nichts": "Nimic de șters: fiecare tură este deja cea mai rapidă pentru mașina, traseul și clasa ei.",
  "frage": "Vor fi șterse {0} ture mai lente și {1} curse neterminate, {2} în total.\n\nRămâne tura ta cea mai rapidă pe traseu, clasă PI și mașină -- start de pe loc și lansat și fiecare mod de joc separat, așa cum le numără recordurile tale. Statisticile curselor rămân complete.\n\nAcest lucru nu poate fi anulat.",
  "fertig": "Șterse: {0} ture. Eliberat: {1}.",
  "fehler": "{0} fișier(e) nu au putut fi șterse -- poate un alt program le ține deschise. Încearcă mai târziu.",
  "hinweis": "Fiecare tură este salvată cu telemetria ei completă. Fila „{0}” deschide dosarul lor și șterge turele mai lente la cerere."},
 "el": {
  "ordner": "Άνοιγμα φακέλου γύρων", "knopf": "Διαγραφή πιο αργών γύρων…", "titel": "Διαγραφή πιο αργών γύρων",
  "nichts": "Τίποτα για διαγραφή: κάθε γύρος είναι ήδη ο ταχύτερος για το αυτοκίνητο, τη διαδρομή και την κατηγορία του.",
  "frage": "Θα διαγραφούν {0} πιο αργοί γύροι και {1} ημιτελείς διαδρομές, συνολικά {2}.\n\nΜένει ο ταχύτερος γύρος σου ανά διαδρομή, κατηγορία PI και αυτοκίνητο -- στατική και ιπτάμενη εκκίνηση και κάθε λειτουργία παιχνιδιού χωριστά, όπως τα μετρούν τα ρεκόρ σου. Τα στατιστικά αγώνων μένουν πλήρη.\n\nΑυτό δεν αναιρείται.",
  "fertig": "Διαγράφηκαν: {0} γύροι. Ελευθερώθηκαν: {1}.",
  "fehler": "{0} αρχείο(α) δεν διαγράφηκαν -- ίσως τα έχει ανοιχτά άλλο πρόγραμμα. Δοκίμασε ξανά αργότερα.",
  "hinweis": "Κάθε γύρος αποθηκεύεται με την πλήρη τηλεμετρία του. Η καρτέλα «{0}» ανοίγει τον φάκελό τους και διαγράφει πιο αργούς γύρους όταν το ζητήσεις."},
 "tr": {
  "ordner": "Tur klasörünü aç", "knopf": "Daha yavaş turları sil…", "titel": "Daha yavaş turları sil",
  "nichts": "Silinecek bir şey yok: her tur zaten kendi aracının, parkurunun ve sınıfının en hızlısı.",
  "frage": "{0} daha yavaş tur ve {1} yarım kalmış koşu silinecek, toplam {2}.\n\nParkur, PI sınıfı ve araç başına en hızlı turun kalır -- duran ve hareketli kalkış ve her oyun modu ayrı, rekorlarının saydığı gibi. Yarış istatistikleri eksiksiz kalır.\n\nBu işlem geri alınamaz.",
  "fertig": "Silindi: {0} tur. Boşaltılan: {1}.",
  "fehler": "{0} dosya silinemedi -- başka bir program açık tutuyor olabilir. Daha sonra tekrar dene.",
  "hinweis": "Her tur, tam telemetrisiyle kaydedilir. \"{0}\" sekmesi klasörlerini açar ve istediğinde daha yavaş turları siler."},
 "ru": {
  "ordner": "Открыть папку кругов", "knopf": "Удалить более медленные круги…", "titel": "Удалить более медленные круги",
  "nichts": "Удалять нечего: каждый круг уже самый быстрый для своей машины, трассы и класса.",
  "frage": "Будут удалены {0} более медленных кругов и {1} незавершённых заездов, всего {2}.\n\nОстанется твой самый быстрый круг для каждой трассы, класса PI и машины -- старт с места и с ходу, а также каждый игровой режим отдельно, как их считают твои рекорды. Статистика гонок останется полной.\n\nЭто нельзя отменить.",
  "fertig": "Удалено: {0} кругов. Освобождено: {1}.",
  "fehler": "Не удалось удалить файлов: {0} -- возможно, их открыла другая программа. Попробуй позже.",
  "hinweis": "Каждый круг сохраняется вместе с полной телеметрией. Вкладка «{0}» открывает их папку и по запросу удаляет более медленные круги."},
 "ja": {
  "ordner": "ラップフォルダーを開く", "knopf": "遅いラップを削除…", "titel": "遅いラップを削除",
  "nichts": "削除するものはありません。どのラップもすでに車・コース・クラスごとの最速です。",
  "frage": "遅いラップ {0} 件と未完走の走行 {1} 件を削除します（合計 {2}）。\n\n残るのは、コース・PI クラス・車ごとの最速ラップです -- スタンディングスタートとフライングスタート、各ゲームモードは記録と同じく別々に扱います。レース統計はそのまま残ります。\n\nこの操作は元に戻せません。",
  "fertig": "削除: {0} ラップ。空き容量: {1}。",
  "fehler": "{0} 個のファイルを削除できませんでした -- 別のプログラムが開いている可能性があります。後でもう一度試してください。",
  "hinweis": "すべてのラップがフルテレメトリー付きで保存されます。「{0}」タブでフォルダーを開いたり、必要に応じて遅いラップを削除したりできます。"},
 "ko": {
  "ordner": "랩 폴더 열기", "knopf": "느린 랩 삭제…", "titel": "느린 랩 삭제",
  "nichts": "삭제할 것이 없습니다. 모든 랩이 이미 차량, 코스, 클래스별 최고 기록입니다.",
  "frage": "느린 랩 {0}개와 완주하지 못한 주행 {1}개를 삭제합니다. 합계 {2}.\n\n코스, PI 클래스, 차량별로 가장 빠른 랩이 남습니다 -- 스탠딩 스타트와 플라잉 스타트, 각 게임 모드는 기록과 마찬가지로 따로 계산합니다. 레이스 통계는 그대로 유지됩니다.\n\n이 작업은 되돌릴 수 없습니다.",
  "fertig": "삭제: 랩 {0}개. 확보: {1}.",
  "fehler": "파일 {0}개를 삭제하지 못했습니다 -- 다른 프로그램이 열고 있을 수 있습니다. 나중에 다시 시도하세요.",
  "hinweis": "모든 랩은 전체 텔레메트리와 함께 저장됩니다. '{0}' 탭에서 폴더를 열고 원할 때 느린 랩을 삭제할 수 있습니다."},
 "zh-Hans": {
  "ordner": "打开圈速文件夹", "knopf": "删除较慢的圈…", "titel": "删除较慢的圈",
  "nichts": "没有可删除的内容：每一圈都已是其车辆、赛道和级别中最快的。",
  "frage": "将删除 {0} 个较慢的圈和 {1} 次未完成的行驶，共 {2}。\n\n保留每条赛道、每个 PI 级别和每辆车的最快一圈 -- 静止起步与行进起步以及各游戏模式分开计算，与你的纪录一致。比赛统计保持完整。\n\n此操作无法撤销。",
  "fertig": "已删除：{0} 圈。释放：{1}。",
  "fehler": "有 {0} 个文件无法删除 -- 可能被其他程序打开。请稍后再试。",
  "hinweis": "每一圈都会连同完整遥测数据一起保存。“{0}”标签页可以打开它们的文件夹，并按需删除较慢的圈。"},
 "zh-Hant": {
  "ordner": "開啟圈速資料夾", "knopf": "刪除較慢的圈…", "titel": "刪除較慢的圈",
  "nichts": "沒有可刪除的內容：每一圈都已是其車輛、賽道和級別中最快的。",
  "frage": "將刪除 {0} 個較慢的圈和 {1} 次未完成的行駛，共 {2}。\n\n保留每條賽道、每個 PI 級別和每輛車的最快一圈 -- 靜止起步與行進起步以及各遊戲模式分開計算，與你的紀錄一致。比賽統計保持完整。\n\n此操作無法復原。",
  "fertig": "已刪除：{0} 圈。釋放：{1}。",
  "fehler": "有 {0} 個檔案無法刪除 -- 可能被其他程式開啟。請稍後再試。",
  "hinweis": "每一圈都會連同完整遙測資料一起儲存。「{0}」分頁可以開啟它們的資料夾，並依需要刪除較慢的圈。"},
 "th": {
  "ordner": "เปิดโฟลเดอร์รอบ", "knopf": "ลบรอบที่ช้ากว่า…", "titel": "ลบรอบที่ช้ากว่า",
  "nichts": "ไม่มีอะไรให้ลบ: ทุกรอบเป็นรอบที่เร็วที่สุดของรถ สนาม และคลาสของมันแล้ว",
  "frage": "จะลบรอบที่ช้ากว่า {0} รอบ และการวิ่งที่ไม่จบ {1} ครั้ง รวม {2}\n\nจะเก็บรอบที่เร็วที่สุดของคุณต่อสนาม คลาส PI และรถ -- การออกตัวจากจุดหยุดนิ่งและออกตัวขณะเคลื่อนที่ รวมถึงแต่ละโหมดเกมนับแยกกัน ตามที่สถิติของคุณนับ สถิติการแข่งยังครบถ้วน\n\nการดำเนินการนี้ย้อนกลับไม่ได้",
  "fertig": "ลบแล้ว: {0} รอบ ได้พื้นที่คืน: {1}",
  "fehler": "ลบไม่ได้ {0} ไฟล์ -- อาจมีโปรแกรมอื่นเปิดอยู่ ลองอีกครั้งภายหลัง",
  "hinweis": "ทุกรอบจะถูกบันทึกพร้อมเทเลเมทรีแบบเต็ม แท็บ \"{0}\" เปิดโฟลเดอร์ของรอบเหล่านั้นและลบรอบที่ช้ากว่าเมื่อคุณต้องการ"},
 "vi": {
  "ordner": "Mở thư mục vòng đua", "knopf": "Xóa các vòng chậm hơn…", "titel": "Xóa các vòng chậm hơn",
  "nichts": "Không có gì để xóa: mỗi vòng đã là vòng nhanh nhất của xe, đường đua và hạng của nó.",
  "frage": "Sẽ xóa {0} vòng chậm hơn và {1} lượt chạy chưa hoàn thành, tổng cộng {2}.\n\nGiữ lại vòng nhanh nhất của bạn cho mỗi đường đua, hạng PI và xe -- xuất phát đứng yên và xuất phát lăn bánh cùng từng chế độ chơi được tính riêng, như cách tính kỷ lục của bạn. Thống kê cuộc đua vẫn đầy đủ.\n\nKhông thể hoàn tác.",
  "fertig": "Đã xóa: {0} vòng. Đã giải phóng: {1}.",
  "fehler": "Không xóa được {0} tệp -- có thể chương trình khác đang mở chúng. Hãy thử lại sau.",
  "hinweis": "Mỗi vòng đều được lưu cùng toàn bộ dữ liệu telemetry. Thẻ \"{0}\" mở thư mục của chúng và xóa các vòng chậm hơn khi bạn muốn."},
 "id": {
  "ordner": "Buka folder lap", "knopf": "Hapus lap yang lebih lambat…", "titel": "Hapus lap yang lebih lambat",
  "nichts": "Tidak ada yang perlu dihapus: setiap lap sudah yang tercepat untuk mobil, rute, dan kelasnya.",
  "frage": "{0} lap yang lebih lambat dan {1} balapan yang tidak selesai akan dihapus, total {2}.\n\nYang disimpan: lap tercepatmu per rute, kelas PI, dan mobil -- start diam dan start melaju serta setiap mode permainan dihitung terpisah, seperti rekormu menghitungnya. Statistik balapan tetap lengkap.\n\nIni tidak dapat dibatalkan.",
  "fertig": "Dihapus: {0} lap. Dibebaskan: {1}.",
  "fehler": "{0} file tidak dapat dihapus -- mungkin sedang dibuka program lain. Coba lagi nanti.",
  "hinweis": "Setiap lap disimpan bersama telemetri lengkapnya. Tab \"{0}\" membuka foldernya dan menghapus lap yang lebih lambat saat kamu minta."},
 "ms": {
  "ordner": "Buka folder pusingan", "knopf": "Padam pusingan yang lebih perlahan…", "titel": "Padam pusingan yang lebih perlahan",
  "nichts": "Tiada apa untuk dipadam: setiap pusingan sudah yang terpantas bagi kereta, laluan dan kelasnya.",
  "frage": "{0} pusingan yang lebih perlahan dan {1} larian yang tidak selesai akan dipadam, jumlahnya {2}.\n\nYang kekal: pusingan terpantas anda bagi setiap laluan, kelas PI dan kereta -- permulaan berhenti dan permulaan bergerak serta setiap mod permainan dikira berasingan, seperti rekod anda mengiranya. Statistik perlumbaan kekal lengkap.\n\nTindakan ini tidak boleh dibuat asal.",
  "fertig": "Dipadam: {0} pusingan. Dikosongkan: {1}.",
  "fehler": "{0} fail tidak dapat dipadam -- mungkin program lain sedang membukanya. Cuba lagi kemudian.",
  "hinweis": "Setiap pusingan disimpan bersama telemetri penuhnya. Tab \"{0}\" membuka foldernya dan memadam pusingan yang lebih perlahan apabila anda minta."},
 "nb": {
  "ordner": "Åpne rundemappen", "knopf": "Slett tregere runder…", "titel": "Slett tregere runder",
  "nichts": "Ingenting å slette: hver runde er allerede den raskeste for sin bil, bane og klasse.",
  "frage": "{0} tregere runder og {1} avbrutte løp blir slettet, {2} totalt.\n\nIgjen blir din raskeste runde per bane, PI-klasse og bil -- stående og flygende start og hver spillmodus for seg, slik rekordene dine teller. Løpsstatistikken forblir komplett.\n\nDette kan ikke angres.",
  "fertig": "Slettet: {0} runder. Frigjort: {1}.",
  "fehler": "{0} fil(er) kunne ikke slettes -- et annet program har dem kanskje åpne. Prøv igjen senere.",
  "hinweis": "Hver runde lagres med full telemetri. Fanen «{0}» åpner mappen deres og sletter tregere runder når du ber om det."},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _text in _saetze.items():
        for _i in range(6):
            assert ("{%d}" % _i in K[_id]) == ("{%d}" % _i in _text), (_code, _id, _i)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
