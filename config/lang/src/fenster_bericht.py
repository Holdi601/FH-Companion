# -*- coding: utf-8 -*-
"""Fenster ohne Titel und der Fensterbericht fuer die Fehlersuche (2026-09-29). 24 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "notitle": "no title",
    "link": "Your window is not in the list? Copy the list of all windows",
    "copied": "The list of all windows is on the clipboard. Paste it into a message to whoever helps you.",
}

_UEBERSETZT = {
 "de": {"notitle": "ohne Titel", "link": "Dein Fenster steht nicht in der Liste? Liste aller Fenster kopieren",
        "copied": "Die Liste aller Fenster liegt in der Zwischenablage. Füge sie in eine Nachricht an den ein, der dir hilft."},
 "fr": {"notitle": "sans titre", "link": "Ta fenêtre n'est pas dans la liste ? Copier la liste de toutes les fenêtres",
        "copied": "La liste de toutes les fenêtres est dans le presse-papiers. Colle-la dans un message à la personne qui t'aide."},
 "es": {"notitle": "sin título", "link": "¿Tu ventana no está en la lista? Copiar la lista de todas las ventanas",
        "copied": "La lista de todas las ventanas está en el portapapeles. Pégala en un mensaje para quien te ayuda."},
 "it": {"notitle": "senza titolo", "link": "La tua finestra non è nell'elenco? Copia l'elenco di tutte le finestre",
        "copied": "L'elenco di tutte le finestre è negli appunti. Incollalo in un messaggio a chi ti aiuta."},
 "pt": {"notitle": "sem título", "link": "A tua janela não está na lista? Copiar a lista de todas as janelas",
        "copied": "A lista de todas as janelas está na área de transferência. Cola-a numa mensagem para quem te ajuda."},
 "nl": {"notitle": "geen titel", "link": "Staat je venster niet in de lijst? Kopieer de lijst van alle vensters",
        "copied": "De lijst van alle vensters staat op het klembord. Plak hem in een bericht aan wie je helpt."},
 "pl": {"notitle": "bez tytułu", "link": "Twojego okna nie ma na liście? Skopiuj listę wszystkich okien",
        "copied": "Lista wszystkich okien jest w schowku. Wklej ją w wiadomości do osoby, która ci pomaga."},
 "cs": {"notitle": "bez názvu", "link": "Tvé okno v seznamu není? Zkopírovat seznam všech oken",
        "copied": "Seznam všech oken je ve schránce. Vlož ho do zprávy pro toho, kdo ti pomáhá."},
 "da": {"notitle": "uden titel", "link": "Er dit vindue ikke på listen? Kopiér listen over alle vinduer",
        "copied": "Listen over alle vinduer ligger i udklipsholderen. Indsæt den i en besked til den, der hjælper dig."},
 "sv": {"notitle": "utan titel", "link": "Finns inte ditt fönster i listan? Kopiera listan över alla fönster",
        "copied": "Listan över alla fönster ligger i urklipp. Klistra in den i ett meddelande till den som hjälper dig."},
 "fi": {"notitle": "ei otsikkoa", "link": "Eikö ikkunasi ole luettelossa? Kopioi kaikkien ikkunoiden luettelo",
        "copied": "Kaikkien ikkunoiden luettelo on leikepöydällä. Liitä se viestiin sille, joka auttaa sinua."},
 "hu": {"notitle": "cím nélkül", "link": "Nincs a listában az ablakod? Az összes ablak listájának másolása",
        "copied": "Az összes ablak listája a vágólapon van. Illeszd be egy üzenetbe annak, aki segít neked."},
 "ro": {"notitle": "fără titlu", "link": "Fereastra ta nu e în listă? Copiază lista tuturor ferestrelor",
        "copied": "Lista tuturor ferestrelor este în clipboard. Lipește-o într-un mesaj către cel care te ajută."},
 "el": {"notitle": "χωρίς τίτλο", "link": "Το παράθυρό σου δεν είναι στη λίστα; Αντιγραφή της λίστας όλων των παραθύρων",
        "copied": "Η λίστα όλων των παραθύρων είναι στο πρόχειρο. Επικόλλησέ την σε ένα μήνυμα προς αυτόν που σε βοηθά."},
 "tr": {"notitle": "başlıksız", "link": "Pencereniz listede yok mu? Tüm pencerelerin listesini kopyala",
        "copied": "Tüm pencerelerin listesi panoda. Sana yardım eden kişiye bir mesajla yapıştır."},
 "ru": {"notitle": "без заголовка", "link": "Твоего окна нет в списке? Скопировать список всех окон",
        "copied": "Список всех окон в буфере обмена. Вставь его в сообщение тому, кто тебе помогает."},
 "ja": {"notitle": "タイトルなし", "link": "ウィンドウがリストにありませんか？全ウィンドウの一覧をコピー",
        "copied": "全ウィンドウの一覧をクリップボードにコピーしました。手伝ってくれる人へのメッセージに貼り付けてください。"},
 "ko": {"notitle": "제목 없음", "link": "창이 목록에 없나요? 모든 창 목록 복사",
        "copied": "모든 창 목록이 클립보드에 있습니다. 도와주는 사람에게 보내는 메시지에 붙여 넣으세요."},
 "zh-Hans": {"notitle": "无标题", "link": "列表里没有你的窗口？复制所有窗口的列表",
             "copied": "所有窗口的列表已复制到剪贴板。把它粘贴到发给帮你的人的消息里。"},
 "zh-Hant": {"notitle": "無標題", "link": "清單裡沒有你的視窗？複製所有視窗的清單",
             "copied": "所有視窗的清單已複製到剪貼簿。把它貼到發給幫你的人的訊息裡。"},
 "th": {"notitle": "ไม่มีชื่อ", "link": "ไม่พบหน้าต่างของคุณในรายการ? คัดลอกรายการหน้าต่างทั้งหมด",
        "copied": "รายการหน้าต่างทั้งหมดอยู่ในคลิปบอร์ดแล้ว วางลงในข้อความถึงคนที่ช่วยคุณ"},
 "vi": {"notitle": "không có tiêu đề", "link": "Cửa sổ của bạn không có trong danh sách? Sao chép danh sách mọi cửa sổ",
        "copied": "Danh sách mọi cửa sổ đã ở trong bộ nhớ tạm. Dán nó vào tin nhắn gửi người đang giúp bạn."},
 "id": {"notitle": "tanpa judul", "link": "Jendelamu tidak ada di daftar? Salin daftar semua jendela",
        "copied": "Daftar semua jendela ada di papan klip. Tempelkan ke pesan untuk orang yang membantumu."},
 "ms": {"notitle": "tiada tajuk", "link": "Tetingkap anda tiada dalam senarai? Salin senarai semua tetingkap",
        "copied": "Senarai semua tetingkap ada dalam papan keratan. Tampalkannya dalam mesej kepada orang yang membantu anda."},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
