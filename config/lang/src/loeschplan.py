# -*- coding: utf-8 -*-
"""Der Loeschplan im Reiter "Tunes" (2026-09-26), 25 Sprachen.

Nicht von Muttersprachlern geprueft. Platzhalter bleiben wortgleich.
"""
import re

K = (
    "Deletion plan",
    "Nothing to delete in this selection.",
    'Press "Check which tunes are on a car" first -- without it the app cannot tell which tunes are safe to delete.',
    "{0} -- {1} tunes",
    "{0} tunes on {1} cars would be deleted, by {2} tuners: {3}",
)

_UEBERSETZT = {
 "de": ("Löschplan", "In dieser Auswahl gibt es nichts zu löschen.",
        'Zuerst "Prüfen, welche Tunes auf einem Auto liegen" drücken -- ohne das weiß die App nicht, welche Tunes gefahrlos weg können.',
        "{0} -- {1} Tunes", "{0} Tunes auf {1} Autos würden gelöscht, von {2} Tunern: {3}"),
 "fr": ("Plan de suppression", "Rien à supprimer dans cette sélection.",
        'Appuie d\'abord sur "Vérifier quels réglages sont sur une voiture" -- sans cela l\'appli ne sait pas lesquels supprimer sans risque.',
        "{0} -- {1} réglages", "{0} réglages sur {1} voitures seraient supprimés, de {2} régleurs : {3}"),
 "es": ("Plan de borrado", "No hay nada que borrar en esta selección.",
        'Pulsa primero "Comprobar qué reglajes están en un coche" -- sin eso la app no sabe cuáles se pueden borrar sin riesgo.',
        "{0} -- {1} reglajes", "Se borrarían {0} reglajes en {1} coches, de {2} preparadores: {3}"),
 "it": ("Piano di eliminazione", "Niente da eliminare in questa selezione.",
        'Premi prima "Controlla quali assetti sono su un\'auto" -- senza, l\'app non sa quali eliminare senza rischi.',
        "{0} -- {1} assetti", "Verrebbero eliminati {0} assetti su {1} auto, di {2} tuner: {3}"),
 "pt": ("Plano de eliminação", "Nada para apagar nesta seleção.",
        'Carrega primeiro em "Verificar que afinações estão num carro" -- sem isso a app não sabe quais pode apagar sem risco.',
        "{0} -- {1} afinações", "Seriam apagadas {0} afinações em {1} carros, de {2} afinadores: {3}"),
 "nl": ("Verwijderplan", "Niets te verwijderen in deze selectie.",
        'Druk eerst op "Controleren welke tunes op een auto staan" -- zonder dat weet de app niet welke veilig weg kunnen.',
        "{0} -- {1} tunes", "{0} tunes op {1} auto's zouden worden verwijderd, van {2} tuners: {3}"),
 "pl": ("Plan usuwania", "W tym wyborze nie ma nic do usunięcia.",
        'Najpierw naciśnij "Sprawdź, które tune\'y są na aucie" -- bez tego aplikacja nie wie, które można bezpiecznie usunąć.',
        "{0} -- {1} tune'ów", "Usunięto by {0} tune'ów na {1} autach, od {2} tunerów: {3}"),
 "sv": ("Borttagningsplan", "Inget att ta bort i det här urvalet.",
        'Tryck först på "Kontrollera vilka tunes som ligger på en bil" -- annars vet appen inte vilka som kan tas bort säkert.',
        "{0} -- {1} tunes", "{0} tunes på {1} bilar skulle tas bort, från {2} tuners: {3}"),
 "da": ("Sletteplan", "Intet at slette i dette udvalg.",
        'Tryk først på "Tjek hvilke tunes der ligger på en bil" -- ellers ved appen ikke, hvilke der trygt kan slettes.',
        "{0} -- {1} tunes", "{0} tunes på {1} biler ville blive slettet, fra {2} tunere: {3}"),
 "fi": ("Poistosuunnitelma", "Tässä valinnassa ei ole poistettavaa.",
        'Paina ensin "Tarkista, mitkä viritykset ovat autossa" -- muuten sovellus ei tiedä, mitkä voi poistaa turvallisesti.',
        "{0} -- {1} viritystä", "{0} viritystä {1} autosta poistettaisiin, {2} virittäjältä: {3}"),
 "cs": ("Plán mazání", "V tomto výběru není co mazat.",
        'Nejdřív stiskni "Zkontrolovat, která nastavení jsou na autě" -- bez toho aplikace neví, co lze bezpečně smazat.',
        "{0} -- {1} nastavení", "Smazalo by se {0} nastavení na {1} autech, od {2} tunerů: {3}"),
 "hu": ("Törlési terv", "Ebben a kiválasztásban nincs mit törölni.",
        'Előbb nyomd meg az "Ellenőrizd, melyik tuning van autón" gombot -- enélkül az alkalmazás nem tudja, mit lehet biztonságosan törölni.',
        "{0} -- {1} tuning", "{0} tuning törlődne {1} autóról, {2} tunertől: {3}"),
 "ro": ("Plan de ștergere", "Nimic de șters în această selecție.",
        'Apasă mai întâi "Verifică ce reglaje sunt pe o mașină" -- fără asta aplicația nu știe ce se poate șterge fără risc.',
        "{0} -- {1} reglaje", "S-ar șterge {0} reglaje de pe {1} mașini, de la {2} tuneri: {3}"),
 "el": ("Σχέδιο διαγραφής", "Τίποτα προς διαγραφή σε αυτή την επιλογή.",
        'Πάτησε πρώτα "Έλεγχος ποια tune είναι σε αυτοκίνητο" -- χωρίς αυτό η εφαρμογή δεν ξέρει ποια διαγράφονται με ασφάλεια.',
        "{0} -- {1} tune", "Θα διαγράφονταν {0} tune σε {1} αυτοκίνητα, από {2} tuners: {3}"),
 "ru": ("План удаления", "В этой выборке нечего удалять.",
        'Сначала нажми "Проверить, какие настройки стоят на машинах" -- без этого приложение не знает, что можно безопасно удалить.',
        "{0} -- настроек: {1}", "Было бы удалено {0} настроек на {1} машинах, от {2} тюнеров: {3}"),
 "tr": ("Silme planı", "Bu seçimde silinecek bir şey yok.",
        'Önce "Hangi ayarların bir araçta olduğunu kontrol et"e bas -- bu olmadan uygulama neyin güvenle silinebileceğini bilemez.',
        "{0} -- {1} ayar", "{1} araçta {0} ayar silinecek, {2} ayarcıdan: {3}"),
 "id": ("Rencana hapus", "Tidak ada yang dihapus dalam pilihan ini.",
        'Tekan dulu "Periksa setelan mana yang terpasang di mobil" -- tanpa itu aplikasi tidak tahu mana yang aman dihapus.',
        "{0} -- {1} setelan", "{0} setelan di {1} mobil akan dihapus, dari {2} tuner: {3}"),
 "ms": ("Pelan pemadaman", "Tiada apa untuk dipadam dalam pilihan ini.",
        'Tekan dahulu "Semak tetapan mana yang ada pada kereta" -- tanpanya aplikasi tidak tahu yang mana selamat dipadam.',
        "{0} -- {1} tetapan", "{0} tetapan pada {1} kereta akan dipadam, daripada {2} penala: {3}"),
 "vi": ("Kế hoạch xóa", "Không có gì để xóa trong lựa chọn này.",
        'Hãy nhấn "Kiểm tra bản tune nào đang gắn trên xe" trước -- nếu không, ứng dụng không biết bản nào xóa an toàn.',
        "{0} -- {1} bản tune", "Sẽ xóa {0} bản tune trên {1} xe, của {2} người tune: {3}"),
 "th": ("แผนการลบ", "ไม่มีอะไรให้ลบในตัวเลือกนี้",
        'กด "ตรวจว่าจูนไหนติดอยู่บนรถ" ก่อน -- ไม่อย่างนั้นแอปไม่รู้ว่าจูนไหนลบได้อย่างปลอดภัย',
        "{0} -- {1} จูน", "จะลบ {0} จูนบนรถ {1} คัน จากผู้จูน {2} คน: {3}"),
 "ja": ("削除プラン", "この選択には削除するものがありません。",
        '先に「どのチューンが車に適用されているか確認」を押してください -- それがないと安全に消せるチューンが分かりません。',
        "{0} -- チューン {1} 件", "{1} 台の車から {0} 件のチューンを削除します（{2} 人のチューナー）: {3}"),
 "ko": ("삭제 계획", "이 선택에는 삭제할 것이 없습니다.",
        '먼저 "어떤 튜닝이 차량에 적용되어 있는지 확인"을 누르세요 -- 그래야 안전하게 지울 튜닝을 알 수 있습니다.',
        "{0} -- 튜닝 {1}개", "차량 {1}대에서 튜닝 {0}개가 삭제됩니다 (튜너 {2}명): {3}"),
 "zh-Hans": ("删除计划", "此选择中没有可删除的内容。",
             '请先按“检查哪些调校装在车上”-- 否则应用不知道哪些可以安全删除。',
             "{0} -- {1} 个调校", "将从 {1} 辆车上删除 {0} 个调校，来自 {2} 位调校者：{3}"),
 "zh-Hant": ("刪除計畫", "此選擇中沒有可刪除的內容。",
             '請先按「檢查哪些調校裝在車上」-- 否則應用不知道哪些可以安全刪除。',
             "{0} -- {1} 個調校", "將從 {1} 輛車上刪除 {0} 個調校，來自 {2} 位調校者：{3}"),
}

_PLATZ = re.compile(r"\{\d+(?::[^}]*)?\}")
for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))
    for _en, _tr in zip(K, _saetze):
        assert sorted(_PLATZ.findall(_en)) == sorted(_PLATZ.findall(_tr)), (_code, _en, _tr)

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
