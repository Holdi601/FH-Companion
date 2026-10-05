# -*- coding: utf-8 -*-
"""Car collection: Garagenautos ohne Gegenstueck in der Liste (2026-09-29), 25 Sprachen. Nicht von Muttersprachlern geprueft."""

K = (
    "{0} cars in your garage are not identified in the list yet -- tick them by hand.",
)

_UEBERSETZT = {
 "de": ("{0} Autos deiner Garage sind in der Liste noch nicht erkannt – hake sie von Hand ab.",),
 "fr": ("{0} voitures de ton garage ne sont pas encore identifiées dans la liste – coche-les à la main.",),
 "es": ("{0} coches de tu garaje aún no están identificados en la lista: márcalos a mano.",),
 "it": ("{0} auto del tuo garage non sono ancora identificate nell'elenco: spuntale a mano.",),
 "pt": ("{0} carros da tua garagem ainda não estão identificados na lista – marca-os à mão.",),
 "nl": ("{0} auto's in je garage zijn nog niet herkend in de lijst – vink ze met de hand aan.",),
 "pl": ("{0} aut z twojego garażu nie rozpoznano jeszcze na liście – zaznacz je ręcznie.",),
 "cs": ("{0} aut z tvé garáže zatím nebylo v seznamu rozpoznáno – zaškrtni je ručně.",),
 "da": ("{0} biler i din garage er endnu ikke genkendt på listen – sæt flueben ved dem manuelt.",),
 "sv": ("{0} bilar i ditt garage är ännu inte identifierade i listan – bocka för dem för hand.",),
 "fi": ("{0} tallisi autoa ei ole vielä tunnistettu listalla – rastita ne käsin.",),
 "hu": ("A garázsod {0} autóját még nem ismeri fel a lista – jelöld be őket kézzel.",),
 "ro": ("{0} mașini din garajul tău nu sunt încă identificate în listă – bifează-le manual.",),
 "el": ("{0} αυτοκίνητα του γκαράζ σου δεν έχουν ακόμη αναγνωριστεί στη λίστα – σημείωσέ τα με το χέρι.",),
 "tr": ("Garajındaki {0} araba listede henüz tanınmadı – onları elle işaretle.",),
 "ru": ("{0} машин из твоего гаража пока не опознаны в списке — отметь их вручную.",),
 "ja": ("ガレージの {0} 台はまだリストで識別されていません。手動でチェックしてください。",),
 "ko": ("차고의 {0}대는 아직 목록에서 식별되지 않았습니다. 직접 체크하세요.",),
 "zh-Hans": ("你车库中有 {0} 辆车尚未在列表中识别——请手动勾选。",),
 "zh-Hant": ("你車庫中有 {0} 輛車尚未在清單中識別——請手動勾選。",),
 "th": ("รถ {0} คันในโรงรถของคุณยังระบุในรายชื่อไม่ได้ – ติ๊กเองด้วยมือ",),
 "vi": ("{0} xe trong gara của bạn chưa được nhận diện trong danh sách – hãy tự đánh dấu.",),
 "id": ("{0} mobil di garasimu belum dikenali di daftar – centang secara manual.",),
 "ms": ("{0} kereta dalam garaj anda belum dikenal pasti dalam senarai – tandakan secara manual.",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))
    assert all("{0}" in s for s in _saetze), _code

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
