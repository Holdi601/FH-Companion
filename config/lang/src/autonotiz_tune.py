# -*- coding: utf-8 -*-
"""Schalter: das aufgespielte Tune in der Autonotiz (2026-09-26), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = ("Show the applied tune's name and description in the car note",)

_UEBERSETZT = {
    "de": ("Name und Beschreibung des aufgespielten Tunes in der Autonotiz zeigen",),
    "fr": ("Afficher le nom et la description du réglage appliqué dans la note de voiture",),
    "es": ("Mostrar el nombre y la descripción del reglaje aplicado en la nota del coche",),
    "it": ("Mostra nome e descrizione dell'assetto applicato nella nota dell'auto",),
    "pt": ("Mostrar o nome e a descrição da afinação aplicada na nota do carro",),
    "nl": ("Naam en beschrijving van de toegepaste tune in de autonotitie tonen",),
    "pl": ("Pokaż nazwę i opis założonego tune'a w notatce o aucie",),
    "sv": ("Visa den applicerade tunens namn och beskrivning i bilanteckningen",),
    "da": ("Vis den anvendte tunes navn og beskrivelse i bilnoten",),
    "fi": ("Näytä käytössä olevan virityksen nimi ja kuvaus automuistiinpanossa",),
    "cs": ("Zobrazit název a popis použitého nastavení v poznámce k autu",),
    "hu": ("Az alkalmazott tuning nevének és leírásának megjelenítése az autós jegyzetben",),
    "ro": ("Arată numele și descrierea reglajului aplicat în nota mașinii",),
    "el": ("Εμφάνιση ονόματος και περιγραφής του εφαρμοσμένου tune στη σημείωση αυτοκινήτου",),
    "ru": ("Показывать название и описание установленной настройки в заметке об авто",),
    "tr": ("Uygulanan ayarın adını ve açıklamasını araç notunda göster",),
    "id": ("Tampilkan nama dan deskripsi setelan terpasang di catatan mobil",),
    "ms": ("Paparkan nama dan penerangan tetapan yang digunakan dalam nota kereta",),
    "vi": ("Hiện tên và mô tả của bản tune đang gắn trong ghi chú xe",),
    "th": ("แสดงชื่อและคำอธิบายของจูนที่ใช้อยู่ในบันทึกรถ",),
    "ja": ("適用中のチューンの名前と説明を車のメモに表示",),
    "ko": ("차량 메모에 적용된 튜닝의 이름과 설명 표시",),
    "zh-Hans": ("在车辆备注中显示已应用调校的名称和说明",),
    "zh-Hant": ("在車輛備註中顯示已套用調校的名稱與說明",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
