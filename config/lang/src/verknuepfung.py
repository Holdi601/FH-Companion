# -*- coding: utf-8 -*-
"""Die zwei Saetze der Schreibtisch-Verknuepfung, in allen 25 Sprachen.

ZUSATZ-Modul nach Schluessel, wie `scanner_europa.py`. Aufbau dort erklaert.

Diese Uebersetzungen sind nicht von Muttersprachlern geprueft.
"""

FRAGE = "Put a shortcut on my desktop"
FEHLER = "The desktop shortcut could not be created."

_UEBERSETZT = {
    "de": ("Eine Verknüpfung auf dem Desktop anlegen",
           "Die Desktop-Verknüpfung konnte nicht angelegt werden."),
    "fr": ("Créer un raccourci sur le bureau",
           "Le raccourci du bureau n'a pas pu être créé."),
    "es": ("Crear un acceso directo en el escritorio",
           "No se pudo crear el acceso directo del escritorio."),
    "it": ("Creare un collegamento sul desktop",
           "Non è stato possibile creare il collegamento sul desktop."),
    "pt": ("Criar um atalho na área de trabalho",
           "Não foi possível criar o atalho na área de trabalho."),
    "nl": ("Een snelkoppeling op het bureaublad maken",
           "De snelkoppeling op het bureaublad kon niet worden gemaakt."),
    "pl": ("Utwórz skrót na pulpicie",
           "Nie udało się utworzyć skrótu na pulpicie."),
    "sv": ("Skapa en genväg på skrivbordet",
           "Genvägen på skrivbordet kunde inte skapas."),
    "da": ("Opret en genvej på skrivebordet",
           "Genvejen på skrivebordet kunne ikke oprettes."),
    "fi": ("Luo pikakuvake työpöydälle",
           "Työpöydän pikakuvaketta ei voitu luoda."),
    "cs": ("Vytvořit zástupce na ploše",
           "Zástupce na ploše se nepodařilo vytvořit."),
    "hu": ("Parancsikon létrehozása az asztalon",
           "Az asztali parancsikont nem sikerült létrehozni."),
    "ro": ("Creează o scurtătură pe desktop",
           "Scurtătura de pe desktop nu a putut fi creată."),
    "el": ("Δημιουργία συντόμευσης στην επιφάνεια εργασίας",
           "Η συντόμευση στην επιφάνεια εργασίας δεν μπόρεσε να δημιουργηθεί."),
    "ru": ("Создать ярлык на рабочем столе",
           "Не удалось создать ярлык на рабочем столе."),
    "tr": ("Masaüstüne kısayol oluştur",
           "Masaüstü kısayolu oluşturulamadı."),
    "id": ("Buat pintasan di desktop",
           "Pintasan desktop tidak dapat dibuat."),
    "ms": ("Cipta pintasan pada desktop",
           "Pintasan desktop tidak dapat dicipta."),
    "vi": ("Tạo lối tắt trên màn hình nền",
           "Không thể tạo lối tắt trên màn hình nền."),
    "th": ("สร้างทางลัดบนเดสก์ท็อป",
           "ไม่สามารถสร้างทางลัดบนเดสก์ท็อปได้"),
    "ja": ("デスクトップにショートカットを作成する",
           "デスクトップのショートカットを作成できませんでした。"),
    "ko": ("바탕 화면에 바로 가기 만들기",
           "바탕 화면 바로 가기를 만들지 못했습니다."),
    "zh-Hans": ("在桌面创建快捷方式",
                "无法创建桌面快捷方式。"),
    "zh-Hant": ("在桌面建立捷徑",
                "無法建立桌面捷徑。"),
}
_UEBERSETZT["zh"] = _UEBERSETZT["zh-Hans"]

ZUSATZ = {code: {FRAGE: frage, FEHLER: fehler}
          for code, (frage, fehler) in _UEBERSETZT.items()}
