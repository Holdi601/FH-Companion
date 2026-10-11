# -*- coding: utf-8 -*-
"""Was der Server von einem Scan verlangt (2026-10-11), 25 Sprachen, nicht von Muttersprachlern
geprueft. "First ranks only" ist der Name des Feldes im Reiter und bleibt in jeder Sprache so,
wie das Feld dort uebersetzt ist -- hier deshalb als Anfuehrung des englischen Namens."""

K = {
    "regel": "A scan reaches the website on its own when it covers at least 3,000 ranks (or the whole board, if it is shorter), its fastest times match the ones already known, and it reads like a real board. Otherwise the server keeps it and the admin decides. Keep \"First ranks only\" at 0 or at least 3,000.",
    "gehalten": "Sent: {0} rows, but held back by the server's checks: {1}",
}

_UEBERSETZT = {
 "de": {"regel": "Ein Scan kommt von selbst auf die Website, wenn er mindestens 3.000 Ränge umfasst (oder das ganze Board, falls es kürzer ist), seine schnellsten Zeiten zu den bekannten passen und er wie ein echtes Board aussieht. Sonst behält ihn der Server, und der Admin entscheidet. Stelle \"First ranks only\" auf 0 oder mindestens 3.000.",
        "gehalten": "Gesendet: {0} Zeilen, aber von den Prüfungen des Servers zurückgehalten: {1}"},
 "fr": {"regel": "Un scan arrive tout seul sur le site s'il couvre au moins 3 000 rangs (ou tout le classement s'il est plus court), si ses meilleurs temps correspondent à ceux déjà connus et s'il ressemble à un vrai classement. Sinon, le serveur le garde et l'admin décide. Laisse \"First ranks only\" à 0 ou à 3 000 au moins.",
        "gehalten": "Envoyé : {0} lignes, mais retenu par les contrôles du serveur : {1}"},
 "es": {"regel": "Un escaneo llega solo a la web si cubre al menos 3.000 puestos (o la clasificación completa, si es más corta), sus mejores tiempos coinciden con los ya conocidos y parece una clasificación real. Si no, el servidor lo guarda y decide el administrador. Deja \"First ranks only\" en 0 o en al menos 3.000.",
        "gehalten": "Enviado: {0} filas, pero retenido por las comprobaciones del servidor: {1}"},
 "it": {"regel": "Una scansione arriva da sola sul sito se copre almeno 3.000 posizioni (o l'intera classifica, se è più corta), i suoi tempi migliori coincidono con quelli già noti e sembra una classifica vera. Altrimenti il server la tiene e decide l'admin. Lascia \"First ranks only\" a 0 o ad almeno 3.000.",
        "gehalten": "Inviato: {0} righe, ma trattenuto dai controlli del server: {1}"},
 "pt": {"regel": "Uma leitura chega sozinha ao site se cobrir pelo menos 3.000 posições (ou a classificação inteira, se for mais curta), se os seus melhores tempos coincidirem com os já conhecidos e se parecer uma classificação real. Caso contrário, o servidor guarda-a e o administrador decide. Deixa \"First ranks only\" em 0 ou em pelo menos 3.000.",
        "gehalten": "Enviado: {0} linhas, mas retido pelas verificações do servidor: {1}"},
 "nl": {"regel": "Een scan komt vanzelf op de website als hij minstens 3.000 plaatsen beslaat (of het hele klassement, als dat korter is), zijn snelste tijden overeenkomen met de bekende en hij eruitziet als een echt klassement. Anders houdt de server hem vast en beslist de beheerder. Zet \"First ranks only\" op 0 of op minstens 3.000.",
        "gehalten": "Verzonden: {0} rijen, maar tegengehouden door de controles van de server: {1}"},
 "pl": {"regel": "Skan trafia na stronę sam, jeśli obejmuje co najmniej 3000 miejsc (albo całą tabelę, jeśli jest krótsza), jego najlepsze czasy zgadzają się ze znanymi i wygląda jak prawdziwa tabela. W przeciwnym razie serwer go zatrzymuje, a decyzję podejmuje administrator. Ustaw \"First ranks only\" na 0 lub co najmniej 3000.",
        "gehalten": "Wysłano: {0} wierszy, ale wstrzymane przez kontrole serwera: {1}"},
 "cs": {"regel": "Sken se na web dostane sám, pokud pokrývá alespoň 3 000 pořadí (nebo celý žebříček, je-li kratší), jeho nejrychlejší časy odpovídají známým a vypadá jako skutečný žebříček. Jinak si ho server ponechá a rozhodne správce. Nech \"First ranks only\" na 0 nebo alespoň na 3 000.",
        "gehalten": "Odesláno: {0} řádků, ale kontroly serveru ho zadržely: {1}"},
 "da": {"regel": "En scanning kommer selv på hjemmesiden, når den dækker mindst 3.000 placeringer (eller hele ranglisten, hvis den er kortere), dens hurtigste tider passer med de kendte, og den ligner en rigtig rangliste. Ellers beholder serveren den, og administratoren bestemmer. Sæt \"First ranks only\" til 0 eller mindst 3.000.",
        "gehalten": "Sendt: {0} rækker, men holdt tilbage af serverens kontroller: {1}"},
 "sv": {"regel": "En skanning kommer till webbplatsen av sig själv när den täcker minst 3 000 placeringar (eller hela topplistan, om den är kortare), dess snabbaste tider stämmer med de kända och den ser ut som en riktig topplista. Annars behåller servern den och administratören avgör. Ställ \"First ranks only\" på 0 eller minst 3 000.",
        "gehalten": "Skickat: {0} rader, men stoppat av serverns kontroller: {1}"},
 "nb": {"regel": "En skanning kommer til nettsiden av seg selv når den dekker minst 3000 plasseringer (eller hele topplisten, hvis den er kortere), de raskeste tidene stemmer med de kjente, og den ser ut som en ekte toppliste. Ellers beholder serveren den, og administratoren avgjør. Sett \"First ranks only\" til 0 eller minst 3000.",
        "gehalten": "Sendt: {0} rader, men holdt tilbake av serverens kontroller: {1}"},
 "fi": {"regel": "Skannaus päätyy sivustolle itsestään, kun se kattaa vähintään 3 000 sijaa (tai koko tulostaulukon, jos se on lyhyempi), sen nopeimmat ajat vastaavat jo tunnettuja ja se näyttää oikealta tulostaulukolta. Muuten palvelin pitää sen ja ylläpitäjä päättää. Pidä \"First ranks only\" arvossa 0 tai vähintään 3 000.",
        "gehalten": "Lähetetty: {0} riviä, mutta palvelimen tarkistukset pidättivät sen: {1}"},
 "hu": {"regel": "Egy beolvasás magától kerül fel a weboldalra, ha legalább 3000 helyezést lefed (vagy a teljes ranglistát, ha az rövidebb), a leggyorsabb idői egyeznek a már ismertekkel, és valódi ranglistának tűnik. Különben a szerver megtartja, és az admin dönt. A \"First ranks only\" értéke legyen 0 vagy legalább 3000.",
        "gehalten": "Elküldve: {0} sor, de a szerver ellenőrzései visszatartották: {1}"},
 "ro": {"regel": "O scanare ajunge singură pe site dacă acoperă cel puțin 3.000 de locuri (sau tot clasamentul, dacă e mai scurt), timpii cei mai buni se potrivesc cu cei deja cunoscuți și arată ca un clasament real. Altfel serverul o păstrează și decide administratorul. Lasă \"First ranks only\" la 0 sau la cel puțin 3.000.",
        "gehalten": "Trimis: {0} rânduri, dar reținut de verificările serverului: {1}"},
 "el": {"regel": "Μια σάρωση φτάνει μόνη της στον ιστότοπο όταν καλύπτει τουλάχιστον 3.000 θέσεις (ή ολόκληρη τη βαθμολογία, αν είναι μικρότερη), οι ταχύτεροι χρόνοι της ταιριάζουν με τους ήδη γνωστούς και μοιάζει με πραγματική βαθμολογία. Αλλιώς ο διακομιστής την κρατά και αποφασίζει ο διαχειριστής. Άφησε το \"First ranks only\" στο 0 ή τουλάχιστον στο 3.000.",
        "gehalten": "Στάλθηκαν: {0} γραμμές, αλλά οι έλεγχοι του διακομιστή τις κράτησαν: {1}"},
 "ru": {"regel": "Скан сам попадает на сайт, если охватывает не меньше 3000 мест (или всю таблицу, если она короче), его лучшие времена совпадают с уже известными и он похож на настоящую таблицу. Иначе сервер оставляет его у себя, и решает администратор. Оставьте \"First ranks only\" на 0 или не меньше 3000.",
        "gehalten": "Отправлено: {0} строк, но проверки сервера задержали скан: {1}"},
 "tr": {"regel": "Bir tarama, en az 3.000 sırayı (ya da daha kısaysa tüm sıralamayı) kapsadığında, en hızlı süreleri bilinenlerle eşleştiğinde ve gerçek bir sıralama gibi göründüğünde siteye kendiliğinden gelir. Aksi halde sunucu onu tutar ve yönetici karar verir. \"First ranks only\" değerini 0 ya da en az 3.000 bırak.",
        "gehalten": "Gönderildi: {0} satır, ancak sunucunun kontrolleri tarafından bekletildi: {1}"},
 "id": {"regel": "Hasil pindai masuk ke situs dengan sendirinya jika mencakup setidaknya 3.000 peringkat (atau seluruh papan, jika lebih pendek), waktu tercepatnya cocok dengan yang sudah dikenal, dan terlihat seperti papan sungguhan. Jika tidak, server menyimpannya dan admin yang memutuskan. Biarkan \"First ranks only\" di 0 atau minimal 3.000.",
        "gehalten": "Terkirim: {0} baris, tetapi ditahan oleh pemeriksaan server: {1}"},
 "ms": {"regel": "Imbasan masuk ke laman web dengan sendirinya jika ia meliputi sekurang-kurangnya 3,000 kedudukan (atau seluruh papan, jika lebih pendek), masa terpantasnya sepadan dengan yang sudah diketahui dan ia kelihatan seperti papan sebenar. Jika tidak, pelayan menyimpannya dan admin membuat keputusan. Biarkan \"First ranks only\" pada 0 atau sekurang-kurangnya 3,000.",
        "gehalten": "Dihantar: {0} baris, tetapi ditahan oleh semakan pelayan: {1}"},
 "vi": {"regel": "Một lần quét tự lên trang web khi nó bao gồm ít nhất 3.000 thứ hạng (hoặc cả bảng nếu bảng ngắn hơn), thời gian nhanh nhất khớp với những gì đã biết và trông giống một bảng thật. Nếu không, máy chủ giữ lại và quản trị viên quyết định. Hãy để \"First ranks only\" ở 0 hoặc ít nhất 3.000.",
        "gehalten": "Đã gửi: {0} dòng, nhưng bị các bước kiểm tra của máy chủ giữ lại: {1}"},
 "th": {"regel": "ผลสแกนจะขึ้นเว็บไซต์เองเมื่อครอบคลุมอย่างน้อย 3,000 อันดับ (หรือทั้งกระดานหากสั้นกว่า) เวลาที่เร็วที่สุดตรงกับที่รู้อยู่แล้ว และดูเหมือนกระดานจริง มิฉะนั้นเซิร์ฟเวอร์จะเก็บไว้และผู้ดูแลเป็นผู้ตัดสิน ตั้ง \"First ranks only\" เป็น 0 หรืออย่างน้อย 3,000",
        "gehalten": "ส่งแล้ว: {0} แถว แต่ถูกการตรวจสอบของเซิร์ฟเวอร์กักไว้: {1}"},
 "ja": {"regel": "スキャンは、3,000位以上（ボードがそれより短い場合はボード全体）を含み、最速タイムが既知のものと一致し、本物のボードらしく見えるとき、自動でサイトに載ります。そうでなければサーバーが保留し、管理者が判断します。「First ranks only」は0か3,000以上にしてください。",
        "gehalten": "送信済み：{0}行。ただしサーバーのチェックで保留されました：{1}"},
 "ko": {"regel": "스캔은 최소 3,000위(보드가 더 짧으면 보드 전체)를 포함하고, 가장 빠른 기록이 이미 알려진 기록과 일치하며, 실제 보드처럼 보이면 자동으로 웹사이트에 올라갑니다. 그렇지 않으면 서버가 보관하고 관리자가 결정합니다. \"First ranks only\"는 0 또는 3,000 이상으로 두세요.",
        "gehalten": "전송됨: {0}행, 하지만 서버 검사에서 보류됨: {1}"},
 "zh-Hans": {"regel": "当扫描至少覆盖 3,000 个名次（若排行榜更短则为整个榜单）、最快成绩与已知成绩相符且看起来像真实榜单时，会自动上传到网站。否则服务器会保留它，由管理员决定。请将“First ranks only”设为 0 或至少 3,000。",
             "gehalten": "已发送：{0} 行，但被服务器的检查暂扣：{1}"},
 "zh-Hant": {"regel": "當掃描至少涵蓋 3,000 個名次（若排行榜較短則為整個榜單）、最快成績與已知成績相符且看起來像真實榜單時，會自動上傳到網站。否則伺服器會保留它，由管理員決定。請將「First ranks only」設為 0 或至少 3,000。",
             "gehalten": "已傳送：{0} 行，但被伺服器的檢查暫扣：{1}"},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _i in range(2):
        assert ("{%d}" % _i in K["gehalten"]) == ("{%d}" % _i in _saetze["gehalten"]), (_code, _i)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
