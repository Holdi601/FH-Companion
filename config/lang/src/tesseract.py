# -*- coding: utf-8 -*-
"""Der Hinweis auf das fehlende Tesseract, in allen 25 Sprachen.

Am 2026-09-19 dazugekommen. Tesseract ist ein eigenstaendiges Programm und kein
pip-Paket: requirements.txt kann es nicht mitbringen, und ohne diese Vorpruefung
faellt sein Fehlen erst auf, wenn eine halbe Stunde Aufnahme schon verbraucht ist.

ZUSATZ-Modul nach Schluessel, wie `verknuepfung_orte.py`. Aufbau dort erklaert.

Die URL und der winget-Befehl bleiben in jeder Sprache unveraendert -- es sind
Befehle, keine Saetze.

Diese Uebersetzungen sind nicht von Muttersprachlern geprueft.
"""

SCHLUESSEL = ("Tesseract OCR is not installed. The leaderboard rows are read "
              "with it, and it is a separate program -- not a Python package. "
              "Install it from https://github.com/UB-Mannheim/tesseract/wiki "
              "or with: winget install -e --id UB-Mannheim.TesseractOCR")

_SCHWANZ = ("https://github.com/UB-Mannheim/tesseract/wiki\n"
            "    winget install -e --id UB-Mannheim.TesseractOCR")

_UEBERSETZT = {
    "de": "Tesseract OCR ist nicht installiert. Damit werden die Zeilen der "
          "Bestenliste gelesen, und es ist ein eigenes Programm -- kein "
          "Python-Paket. Zu holen unter: ",
    "fr": "Tesseract OCR n'est pas installé. Les lignes du classement sont lues "
          "avec, et c'est un programme distinct -- pas un paquet Python. "
          "À installer depuis : ",
    "es": "Tesseract OCR no está instalado. Con él se leen las filas de la "
          "clasificación, y es un programa aparte, no un paquete de Python. "
          "Instálalo desde: ",
    "it": "Tesseract OCR non è installato. Serve a leggere le righe della "
          "classifica ed è un programma a sé, non un pacchetto Python. "
          "Installalo da: ",
    "pt": "O Tesseract OCR não está instalado. É com ele que as linhas da "
          "classificação são lidas, e é um programa à parte, não um pacote "
          "Python. Instale a partir de: ",
    "nl": "Tesseract OCR is niet geïnstalleerd. Daarmee worden de regels van de "
          "ranglijst gelezen, en het is een apart programma -- geen Python-pakket. "
          "Installeer het via: ",
    "pl": "Tesseract OCR nie jest zainstalowany. Służy do odczytu wierszy "
          "tabeli wyników i jest osobnym programem, a nie pakietem Pythona. "
          "Zainstaluj go z: ",
    "sv": "Tesseract OCR är inte installerat. Raderna i resultatlistan läses med "
          "det, och det är ett eget program -- inget Python-paket. "
          "Installera det från: ",
    "da": "Tesseract OCR er ikke installeret. Rækkerne i ranglisten læses med "
          "det, og det er et selvstændigt program -- ikke en Python-pakke. "
          "Installér det fra: ",
    "fi": "Tesseract OCR:ää ei ole asennettu. Sillä luetaan tulosluettelon rivit, "
          "ja se on erillinen ohjelma -- ei Python-paketti. Asenna se osoitteesta: ",
    "cs": "Tesseract OCR není nainstalován. Čtou se jím řádky žebříčku a je to "
          "samostatný program, nikoli balíček Pythonu. Nainstalujte jej z: ",
    "hu": "A Tesseract OCR nincs telepítve. Ezzel olvassuk a ranglista sorait, és "
          "ez külön program -- nem Python-csomag. Telepítse innen: ",
    "ro": "Tesseract OCR nu este instalat. Cu el sunt citite rândurile "
          "clasamentului și este un program separat, nu un pachet Python. "
          "Instalează-l de la: ",
    "el": "Το Tesseract OCR δεν είναι εγκατεστημένο. Με αυτό διαβάζονται οι "
          "γραμμές του πίνακα κατάταξης, και είναι ξεχωριστό πρόγραμμα -- όχι "
          "πακέτο Python. Εγκαταστήστε το από: ",
    "ru": "Tesseract OCR не установлен. Им читаются строки таблицы результатов, "
          "и это отдельная программа, а не пакет Python. Установите его отсюда: ",
    "tr": "Tesseract OCR kurulu değil. Sıralama satırları onunla okunur ve ayrı "
          "bir programdır -- Python paketi değil. Şuradan kurun: ",
    "id": "Tesseract OCR belum terpasang. Baris papan peringkat dibaca dengannya, "
          "dan ini program terpisah -- bukan paket Python. Pasang dari: ",
    "ms": "Tesseract OCR tidak dipasang. Baris papan pendahulu dibaca dengannya, "
          "dan ia program berasingan -- bukan pakej Python. Pasang daripada: ",
    "vi": "Tesseract OCR chưa được cài. Các dòng bảng xếp hạng được đọc bằng nó, "
          "và đây là một chương trình riêng -- không phải gói Python. Cài từ: ",
    "th": "ยังไม่ได้ติดตั้ง Tesseract OCR ระบบใช้มันอ่านแถวของกระดานอันดับ "
          "และมันเป็นโปรแกรมแยกต่างหาก ไม่ใช่แพ็กเกจ Python ติดตั้งได้จาก: ",
    "ja": "Tesseract OCR がインストールされていません。リーダーボードの行はこれで"
          "読み取ります。Python パッケージではなく独立したプログラムです。"
          "次から入手してください: ",
    "ko": "Tesseract OCR이 설치되어 있지 않습니다. 순위표의 행을 이것으로 읽으며, "
          "Python 패키지가 아니라 별도의 프로그램입니다. 다음에서 설치하세요: ",
    "zh-Hans": "未安装 Tesseract OCR。排行榜的行由它读取，它是独立程序，"
               "不是 Python 包。请从这里安装：",
    "zh-Hant": "未安裝 Tesseract OCR。排行榜的列由它讀取，它是獨立程式，"
               "不是 Python 套件。請從這裡安裝：",
}
_UEBERSETZT["zh"] = _UEBERSETZT["zh-Hans"]

ZUSATZ = {code: {SCHLUESSEL: text + _SCHWANZ}
          for code, text in _UEBERSETZT.items()}
