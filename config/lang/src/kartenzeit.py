# -*- coding: utf-8 -*-
"""Wie lange die Anmeldekarten stehen (2026-09-26), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = ("Sign-up maps disappear after (seconds, 0 = when the race starts)",)

_UEBERSETZT = {
    "de": ("Anmeldekarten verschwinden nach (Sekunden, 0 = beim Rennstart)",),
    "fr": ("Les cartes d'inscription disparaissent après (secondes, 0 = au départ de la course)",),
    "es": ("Los mapas de inscripción desaparecen tras (segundos, 0 = al empezar la carrera)",),
    "it": ("Le mappe d'iscrizione spariscono dopo (secondi, 0 = alla partenza della gara)",),
    "pt": ("Os mapas de inscrição desaparecem após (segundos, 0 = quando a corrida começa)",),
    "nl": ("Inschrijfkaarten verdwijnen na (seconden, 0 = bij de start van de race)",),
    "pl": ("Mapy zapisów znikają po (sekundy, 0 = przy starcie wyścigu)",),
    "sv": ("Anmälningskartor försvinner efter (sekunder, 0 = när loppet startar)",),
    "da": ("Tilmeldingskort forsvinder efter (sekunder, 0 = når løbet starter)",),
    "fi": ("Ilmoittautumiskartat katoavat (sekuntia, 0 = kun kilpailu alkaa)",),
    "cs": ("Mapy přihlášení zmizí po (sekundy, 0 = při startu závodu)",),
    "hu": ("A nevezési térképek eltűnnek ennyi idő után (másodperc, 0 = a verseny rajtjánál)",),
    "ro": ("Hărțile de înscriere dispar după (secunde, 0 = la startul cursei)",),
    "el": ("Οι χάρτες εγγραφής εξαφανίζονται μετά από (δευτερόλεπτα, 0 = στην εκκίνηση του αγώνα)",),
    "ru": ("Карты записи исчезают через (секунды, 0 = при старте гонки)",),
    "tr": ("Kayıt haritaları şu süre sonra kaybolur (saniye, 0 = yarış başlayınca)",),
    "id": ("Peta pendaftaran hilang setelah (detik, 0 = saat balapan dimulai)",),
    "ms": ("Peta pendaftaran hilang selepas (saat, 0 = apabila perlumbaan bermula)",),
    "vi": ("Bản đồ đăng ký biến mất sau (giây, 0 = khi cuộc đua bắt đầu)",),
    "th": ("แผนที่ลงทะเบียนหายไปหลังจาก (วินาที, 0 = เมื่อเริ่มแข่ง)",),
    "ja": ("エントリーマップを消すまでの時間（秒、0 = レース開始時）",),
    "ko": ("참가 신청 지도가 사라지는 시간 (초, 0 = 레이스 시작 시)",),
    "zh-Hans": ("报名地图在多久后消失（秒，0 = 比赛开始时）",),
    "zh-Hant": ("報名地圖在多久後消失（秒，0 = 比賽開始時）",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
