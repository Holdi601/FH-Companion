# -*- coding: utf-8 -*-
"""Der Hinweis des Scan-Werkzeugs, wenn es auf HTTP zurueckfaellt (2026-09-25),
25 Sprachen.

Das Werkzeug laedt seit dem 2026-09-25 ueber HTTPS hoch. Kann ein Rechner das
Zertifikat nicht pruefen, faellt es auf HTTP zurueck, wie bis dahin immer -- und
sagt das in einem Satz (contrib_scan.py, upload()).

Nicht von Muttersprachlern geprueft.
"""

A = ("The server's certificate could not be checked on this PC -- sending over "
     "plain http, as before.")

_UEBERSETZT = {
 "de": "Das Zertifikat des Servers ließ sich auf diesem PC nicht prüfen -- es wird wie bisher über einfaches http gesendet.",
 "fr": "Le certificat du serveur n'a pas pu être vérifié sur ce PC -- envoi en http simple, comme avant.",
 "es": "No se pudo comprobar el certificado del servidor en este PC: se envía por http sin cifrar, como antes.",
 "it": "Non è stato possibile verificare il certificato del server su questo PC: invio tramite http semplice, come prima.",
 "pt": "Não foi possível verificar o certificado do servidor neste PC -- a enviar por http simples, como antes.",
 "nl": "Het certificaat van de server kon op deze pc niet worden gecontroleerd -- verzenden via gewoon http, zoals voorheen.",
 "pl": "Nie udało się sprawdzić certyfikatu serwera na tym komputerze -- wysyłanie zwykłym http, jak dotąd.",
 "sv": "Serverns certifikat kunde inte kontrolleras på den här datorn -- skickar över vanlig http, som tidigare.",
 "da": "Serverens certifikat kunne ikke kontrolleres på denne pc -- sender over almindelig http som hidtil.",
 "fi": "Palvelimen varmennetta ei voitu tarkistaa tällä tietokoneella -- lähetetään tavallisella http:llä kuten ennenkin.",
 "cs": "Certifikát serveru se na tomto počítači nepodařilo ověřit -- odesílá se přes obyčejné http jako dosud.",
 "hu": "A szerver tanúsítványát ezen a gépen nem sikerült ellenőrizni -- küldés sima http-n, mint eddig.",
 "ro": "Certificatul serverului nu a putut fi verificat pe acest PC -- se trimite prin http simplu, ca înainte.",
 "el": "Το πιστοποιητικό του διακομιστή δεν μπόρεσε να ελεγχθεί σε αυτόν τον υπολογιστή -- αποστολή μέσω απλού http, όπως πριν.",
 "ru": "Не удалось проверить сертификат сервера на этом ПК -- отправка по обычному http, как раньше.",
 "tr": "Sunucunun sertifikası bu bilgisayarda doğrulanamadı -- eskisi gibi düz http üzerinden gönderiliyor.",
 "id": "Sertifikat server tidak dapat diperiksa di PC ini -- dikirim melalui http biasa, seperti sebelumnya.",
 "ms": "Sijil pelayan tidak dapat diperiksa pada PC ini -- dihantar melalui http biasa, seperti sebelum ini.",
 "vi": "Không thể kiểm tra chứng chỉ của máy chủ trên máy này -- gửi qua http thường như trước đây.",
 "th": "ไม่สามารถตรวจสอบใบรับรองของเซิร์ฟเวอร์บนพีซีเครื่องนี้ได้ -- จะส่งผ่าน http ธรรมดาเหมือนเดิม",
 "ja": "この PC ではサーバーの証明書を確認できませんでした。これまでどおり通常の http で送信します。",
 "ko": "이 PC에서 서버 인증서를 확인할 수 없습니다. 이전처럼 일반 http로 보냅니다.",
 "zh-Hans": "无法在此电脑上验证服务器证书，将像以前一样通过普通 http 发送。",
 "zh-Hant": "無法在此電腦上驗證伺服器憑證，將像以前一樣透過一般 http 傳送。",
}

ZUSATZ = {code: {A: text} for code, text in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
