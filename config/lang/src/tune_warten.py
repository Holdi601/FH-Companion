# -*- coding: utf-8 -*-
"""Das Loeschen im Spiel wartet auf den Cars-Reiter (2026-09-26), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = ("Waiting for the game's pause menu on the CARS tab ...",)

_UEBERSETZT = {
 "de": ("Warte auf das Pausenmenü des Spiels im Reiter AUTOS ...",),
 "fr": ("En attente du menu pause du jeu sur l'onglet VOITURES ...",),
 "es": ("Esperando el menú de pausa del juego en la pestaña COCHES ...",),
 "it": ("In attesa del menu di pausa del gioco sulla scheda AUTO ...",),
 "pt": ("Aguardando o menu de pausa do jogo no separador CARROS ...",),
 "nl": ("Wachten op het pauzemenu van de game op het tabblad AUTO'S ...",),
 "pl": ("Czekam na menu pauzy gry na karcie SAMOCHODY ...",),
 "sv": ("Väntar på spelets pausmeny på fliken BILAR ...",),
 "da": ("Venter på spillets pausemenu på fanen BILER ...",),
 "fi": ("Odotetaan pelin taukovalikkoa AUTOT-välilehdellä ...",),
 "cs": ("Čekám na menu pauzy hry na kartě AUTA ...",),
 "hu": ("Várakozás a játék szünetmenüjére az AUTÓK fülön ...",),
 "ro": ("Se așteaptă meniul de pauză al jocului pe fila MAȘINI ...",),
 "el": ("Αναμονή για το μενού παύσης του παιχνιδιού στην καρτέλα ΑΥΤΟΚΙΝΗΤΑ ...",),
 "ru": ("Ожидание меню паузы игры на вкладке АВТОМОБИЛИ ...",),
 "tr": ("Oyunun ARAÇLAR sekmesindeki duraklatma menüsü bekleniyor ...",),
 "id": ("Menunggu menu jeda game di tab MOBIL ...",),
 "ms": ("Menunggu menu jeda permainan pada tab KERETA ...",),
 "vi": ("Đang chờ menu tạm dừng của game ở thẻ XE ...",),
 "th": ("กำลังรอเมนูหยุดชั่วคราวของเกมที่แท็บ รถ ...",),
 "ja": ("ゲームの「車」タブのポーズメニューを待っています ...",),
 "ko": ("게임의 차량 탭 일시 정지 메뉴를 기다리는 중 ...",),
 "zh-Hans": ("正在等待游戏“车辆”选项卡的暂停菜单 ...",),
 "zh-Hant": ("正在等待遊戲「車輛」分頁的暫停選單 ...",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
