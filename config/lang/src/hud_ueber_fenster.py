# -*- coding: utf-8 -*-
"""Konsolenmodus: der HUD ueber dem Fenster von Xbox Remote Play oder dem OBS-Projektor (2026-09-29), 25 Sprachen.

"Lap delta HUD" ist der Name eines Reiters und bleibt englisch wie dort. Nicht von
Muttersprachlern geprueft.
"""

K = (
    "Where the HUD shows",
    "In the dashboard window",
    "Over the game window, like playing on the PC",
    "It shows while that window is in front. Move and size the HUD in the Lap delta HUD tab, as on the PC.",
)

_UEBERSETZT = {
 "de": ("Wo der HUD erscheint", "Im Dashboard-Fenster", "Über dem Spielfenster, wie beim Spielen am PC",
        "Er erscheint, solange dieses Fenster vorne ist. Lage und Größe stellst du im Reiter Lap delta HUD ein, wie am PC."),
 "fr": ("Où s'affiche le HUD", "Dans la fenêtre tableau de bord", "Par-dessus la fenêtre du jeu, comme sur PC",
        "Il s'affiche tant que cette fenêtre est au premier plan. Place et taille du HUD : onglet Lap delta HUD, comme sur PC."),
 "es": ("Dónde se muestra el HUD", "En la ventana del panel", "Sobre la ventana del juego, como jugando en el PC",
        "Aparece mientras esa ventana está delante. Posición y tamaño del HUD en la pestaña Lap delta HUD, como en el PC."),
 "it": ("Dove appare l'HUD", "Nella finestra dashboard", "Sopra la finestra del gioco, come giocando sul PC",
        "Appare finché quella finestra è in primo piano. Posizione e dimensione dell'HUD nella scheda Lap delta HUD, come sul PC."),
 "pt": ("Onde o HUD aparece", "Na janela do painel", "Sobre a janela do jogo, como a jogar no PC",
        "Aparece enquanto essa janela estiver à frente. Posição e tamanho do HUD no separador Lap delta HUD, como no PC."),
 "nl": ("Waar de HUD verschijnt", "In het dashboardvenster", "Over het spelvenster, zoals spelen op de pc",
        "Hij verschijnt zolang dat venster vooraan staat. Plaats en grootte van de HUD stel je in op het tabblad Lap delta HUD, zoals op de pc."),
 "pl": ("Gdzie pojawia się HUD", "W oknie panelu", "Nad oknem gry, jak przy grze na PC",
        "Pojawia się, gdy to okno jest na wierzchu. Położenie i rozmiar HUD ustawisz w karcie Lap delta HUD, jak na PC."),
 "cs": ("Kde se HUD zobrazí", "V okně panelu", "Nad oknem hry, jako při hraní na PC",
        "Zobrazí se, dokud je toto okno vpředu. Polohu a velikost HUD nastavíš na kartě Lap delta HUD, jako na PC."),
 "da": ("Hvor HUD'en vises", "I dashboard-vinduet", "Over spilvinduet, som når du spiller på pc'en",
        "Den vises, mens det vindue er forrest. Placering og størrelse af HUD'en indstiller du under Lap delta HUD, som på pc'en."),
 "sv": ("Var HUD:en visas", "I instrumentpanelfönstret", "Över spelfönstret, som när du spelar på datorn",
        "Den visas så länge fönstret ligger överst. Placering och storlek ställer du in under fliken Lap delta HUD, som på datorn."),
 "fi": ("Missä HUD näkyy", "Kojelautaikkunassa", "Peli-ikkunan päällä, kuten PC:llä pelatessa",
        "Se näkyy, kun ikkuna on edessä. HUDin paikan ja koon asetat Lap delta HUD -välilehdellä, kuten PC:llä."),
 "hu": ("Hol jelenik meg a HUD", "A műszerfal-ablakban", "A játékablak fölött, mint PC-n játszva",
        "Akkor látszik, amikor az az ablak van elöl. A HUD helyét és méretét a Lap delta HUD lapon állítod, mint PC-n."),
 "ro": ("Unde apare HUD-ul", "În fereastra de panou", "Peste fereastra jocului, ca atunci când joci pe PC",
        "Apare cât timp fereastra este în față. Poziția și mărimea HUD-ului le setezi în fila Lap delta HUD, ca pe PC."),
 "el": ("Πού εμφανίζεται το HUD", "Στο παράθυρο πίνακα", "Πάνω από το παράθυρο του παιχνιδιού, όπως στο PC",
        "Εμφανίζεται όσο αυτό το παράθυρο είναι μπροστά. Θέση και μέγεθος του HUD ορίζονται στην καρτέλα Lap delta HUD, όπως στο PC."),
 "tr": ("HUD nerede görünür", "Gösterge paneli penceresinde", "Oyun penceresinin üstünde, PC'de oynar gibi",
        "O pencere öndeyken görünür. HUD'un yerini ve boyutunu PC'deki gibi Lap delta HUD sekmesinde ayarlarsın."),
 "ru": ("Где показывается HUD", "В окне панели", "Поверх окна игры, как при игре на ПК",
        "Он виден, пока это окно на переднем плане. Положение и размер HUD задаются на вкладке Lap delta HUD, как на ПК."),
 "ja": ("HUD の表示場所", "ダッシュボードウィンドウ内", "PC でプレイするときのようにゲームウィンドウの上",
        "そのウィンドウが前面にある間だけ表示されます。HUD の位置と大きさは PC と同じく Lap delta HUD タブで設定します。"),
 "ko": ("HUD 표시 위치", "대시보드 창 안", "PC에서 플레이할 때처럼 게임 창 위",
        "그 창이 앞에 있는 동안 표시됩니다. HUD 위치와 크기는 PC처럼 Lap delta HUD 탭에서 설정합니다."),
 "zh-Hans": ("HUD 显示位置", "在仪表板窗口中", "在游戏窗口上方，就像在电脑上玩一样",
             "只要该窗口在前台就会显示。HUD 的位置和大小与在电脑上一样，在 Lap delta HUD 标签页中设置。"),
 "zh-Hant": ("HUD 顯示位置", "在儀表板視窗中", "在遊戲視窗上方，就像在電腦上玩一樣",
             "只要該視窗在前景就會顯示。HUD 的位置和大小與在電腦上一樣，在 Lap delta HUD 分頁中設定。"),
 "th": ("ตำแหน่งที่ HUD แสดง", "ในหน้าต่างแดชบอร์ด", "บนหน้าต่างเกม เหมือนเล่นบนพีซี",
        "แสดงขณะที่หน้าต่างนั้นอยู่ด้านหน้า ปรับตำแหน่งและขนาด HUD ได้ในแท็บ Lap delta HUD เหมือนบนพีซี"),
 "vi": ("Nơi HUD hiển thị", "Trong cửa sổ bảng điều khiển", "Trên cửa sổ trò chơi, như khi chơi trên PC",
        "HUD hiện khi cửa sổ đó ở phía trước. Vị trí và kích thước HUD chỉnh trong thẻ Lap delta HUD, như trên PC."),
 "id": ("Tempat HUD tampil", "Di jendela dasbor", "Di atas jendela game, seperti bermain di PC",
        "Tampil selama jendela itu di depan. Posisi dan ukuran HUD diatur di tab Lap delta HUD, seperti di PC."),
 "ms": ("Tempat HUD dipaparkan", "Dalam tetingkap papan pemuka", "Di atas tetingkap permainan, seperti bermain di PC",
        "Ia dipaparkan selagi tetingkap itu di hadapan. Kedudukan dan saiz HUD ditetapkan dalam tab Lap delta HUD, seperti di PC."),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
