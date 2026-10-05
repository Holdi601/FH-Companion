# -*- coding: utf-8 -*-
"""Fenster ohne Titel und der Fensterbericht fuer die Fehlersuche (2026-09-29). 24 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "notitle": "no title",
    "link": "Your window is not in the list? Copy the list of all windows"
}

_UEBERSETZT = {
 "de": {"notitle": "ohne Titel", "link": "Dein Fenster steht nicht in der Liste? Liste aller Fenster kopieren"
        },
 "fr": {"notitle": "sans titre", "link": "Ta fenêtre n'est pas dans la liste ? Copier la liste de toutes les fenêtres"
        },
 "es": {"notitle": "sin título", "link": "¿Tu ventana no está en la lista? Copiar la lista de todas las ventanas"
        },
 "it": {"notitle": "senza titolo", "link": "La tua finestra non è nell'elenco? Copia l'elenco di tutte le finestre"
        },
 "pt": {"notitle": "sem título", "link": "A tua janela não está na lista? Copiar a lista de todas as janelas"
        },
 "nl": {"notitle": "geen titel", "link": "Staat je venster niet in de lijst? Kopieer de lijst van alle vensters"
        },
 "pl": {"notitle": "bez tytułu", "link": "Twojego okna nie ma na liście? Skopiuj listę wszystkich okien"
        },
 "cs": {"notitle": "bez názvu", "link": "Tvé okno v seznamu není? Zkopírovat seznam všech oken"
        },
 "da": {"notitle": "uden titel", "link": "Er dit vindue ikke på listen? Kopiér listen over alle vinduer"
        },
 "sv": {"notitle": "utan titel", "link": "Finns inte ditt fönster i listan? Kopiera listan över alla fönster"
        },
 "fi": {"notitle": "ei otsikkoa", "link": "Eikö ikkunasi ole luettelossa? Kopioi kaikkien ikkunoiden luettelo"
        },
 "hu": {"notitle": "cím nélkül", "link": "Nincs a listában az ablakod? Az összes ablak listájának másolása"
        },
 "ro": {"notitle": "fără titlu", "link": "Fereastra ta nu e în listă? Copiază lista tuturor ferestrelor"
        },
 "el": {"notitle": "χωρίς τίτλο", "link": "Το παράθυρό σου δεν είναι στη λίστα; Αντιγραφή της λίστας όλων των παραθύρων"
        },
 "tr": {"notitle": "başlıksız", "link": "Pencereniz listede yok mu? Tüm pencerelerin listesini kopyala"
        },
 "ru": {"notitle": "без заголовка", "link": "Твоего окна нет в списке? Скопировать список всех окон"
        },
 "ja": {"notitle": "タイトルなし", "link": "ウィンドウがリストにありませんか？全ウィンドウの一覧をコピー"
        },
 "ko": {"notitle": "제목 없음", "link": "창이 목록에 없나요? 모든 창 목록 복사"
        },
 "zh-Hans": {"notitle": "无标题", "link": "列表里没有你的窗口？复制所有窗口的列表"
             },
 "zh-Hant": {"notitle": "無標題", "link": "清單裡沒有你的視窗？複製所有視窗的清單"
             },
 "th": {"notitle": "ไม่มีชื่อ", "link": "ไม่พบหน้าต่างของคุณในรายการ? คัดลอกรายการหน้าต่างทั้งหมด"
        },
 "vi": {"notitle": "không có tiêu đề", "link": "Cửa sổ của bạn không có trong danh sách? Sao chép danh sách mọi cửa sổ"
        },
 "id": {"notitle": "tanpa judul", "link": "Jendelamu tidak ada di daftar? Salin daftar semua jendela"
        },
 "ms": {"notitle": "tiada tajuk", "link": "Tetingkap anda tiada dalam senarai? Salin senarai semua tetingkap"
        }
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
