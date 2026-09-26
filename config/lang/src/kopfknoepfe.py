# -*- coding: utf-8 -*-
"""Die Knoepfe "Desktop shortcut" und "Pin to taskbar" im Kopf des Hauptfensters
(2026-09-25), 25 Sprachen.

Im langen Hinweis steht in jeder Sprache GENAU der Menuepunkt, den ein Windows in
dieser Sprache beim Rechtsklick zeigt ("An Taskleiste anheften", "Épingler à la
barre des tâches" ...) -- sonst sucht der Nutzer einen Eintrag, den es so nicht
gibt. Die Knopfbeschriftungen duerfen kuerzer sein, wo der Windows-Begriff nicht
in die Zeile passt (Italienisch, Griechisch).

Nicht von Muttersprachlern geprueft.
"""

A = "Desktop shortcut"
B = "Desktop shortcut ✓"
C = "Pin to taskbar"
D = "Pinned to taskbar ✓"
E = ("Puts a shortcut to this copy of the program on your desktop. One that points to "
     "a moved or deleted copy is replaced.")
F = "Windows leaves pinning to you. This prepares everything and shows the one click it takes."
G = "The Start menu entry could not be created."
H = ("Windows only lets you pin programs yourself, so this takes one click from you: "
     "right-click this program's icon in the taskbar and choose “Pin to taskbar”. The "
     "Start menu entry it needs is in place, and this button shows a tick once the pin "
     "is there.")

# kennung: (A, B, C, D, E, F, G, H)
_UEBERSETZT = {
 "de": ("Desktop-Verknüpfung", "Desktop-Verknüpfung ✓", "An Taskleiste anheften",
        "An Taskleiste angeheftet ✓",
        "Legt eine Verknüpfung zu dieser Kopie des Programms auf den Desktop. Eine, die auf eine verschobene oder gelöschte Kopie zeigt, wird ersetzt.",
        "Das Anheften überlässt Windows dir. Dieser Knopf bereitet alles vor und zeigt den einen Klick, der noch fehlt.",
        "Der Startmenü-Eintrag konnte nicht angelegt werden.",
        "Windows lässt das Anheften nur dich selbst machen, darum braucht es einen Klick von dir: Rechtsklick auf das Symbol dieses Programms in der Taskleiste, dann „An Taskleiste anheften“. Der nötige Startmenü-Eintrag ist angelegt, und dieser Knopf zeigt ein Häkchen, sobald die Anheftung da ist."),
 "fr": ("Raccourci bureau", "Raccourci bureau ✓", "Épingler à la barre des tâches",
        "Épinglé à la barre des tâches ✓",
        "Place sur le bureau un raccourci vers cette copie du programme. Un raccourci qui pointe vers une copie déplacée ou supprimée est remplacé.",
        "Windows te laisse le soin d'épingler. Ce bouton prépare tout et montre le seul clic nécessaire.",
        "L'entrée du menu Démarrer n'a pas pu être créée.",
        "Windows ne laisse que toi épingler des programmes, il te faut donc un clic : clic droit sur l'icône de ce programme dans la barre des tâches, puis « Épingler à la barre des tâches ». L'entrée du menu Démarrer nécessaire est en place, et ce bouton affiche une coche dès que l'épingle est là."),
 "es": ("Icono en el escritorio", "Icono en el escritorio ✓", "Anclar a la barra de tareas",
        "Anclado a la barra de tareas ✓",
        "Pone en el escritorio un acceso directo a esta copia del programa. Si uno apunta a una copia movida o borrada, se sustituye.",
        "Windows deja el anclaje en tus manos. Esto lo prepara todo y te muestra el único clic que hace falta.",
        "No se pudo crear la entrada del menú Inicio.",
        "Windows solo te deja anclar programas a ti, así que falta un clic tuyo: haz clic derecho en el icono de este programa en la barra de tareas y elige «Anclar a la barra de tareas». La entrada del menú Inicio que necesita ya está creada, y este botón muestra una marca en cuanto quede anclado."),
 "it": ("Collegamento sul desktop", "Collegamento sul desktop ✓", "Aggiungi alla barra",
        "Aggiunto alla barra ✓",
        "Mette sul desktop un collegamento a questa copia del programma. Uno che punta a una copia spostata o eliminata viene sostituito.",
        "Windows lascia a te l'aggiunta alla barra. Questo prepara tutto e mostra l'unico clic necessario.",
        "Non è stato possibile creare la voce del menu Start.",
        "Windows consente solo a te di aggiungere programmi alla barra, quindi serve un tuo clic: fai clic destro sull'icona di questo programma nella barra delle applicazioni e scegli «Aggiungi alla barra delle applicazioni». La voce del menu Start necessaria è pronta, e questo pulsante mostra un segno di spunta appena il programma è aggiunto."),
 "pt": ("Atalho no ambiente de trabalho", "Atalho no ambiente de trabalho ✓",
        "Afixar na barra de tarefas", "Afixado na barra de tarefas ✓",
        "Coloca no ambiente de trabalho um atalho para esta cópia do programa. Um atalho que aponte para uma cópia movida ou apagada é substituído.",
        "O Windows deixa a afixação contigo. Isto prepara tudo e mostra o único clique necessário.",
        "Não foi possível criar a entrada do menu Iniciar.",
        "O Windows só deixa que sejas tu a afixar programas, por isso falta um clique teu: clica com o botão direito no ícone deste programa na barra de tarefas e escolhe «Afixar na barra de tarefas». A entrada do menu Iniciar necessária já existe, e este botão mostra um visto assim que estiver afixado."),
 "nl": ("Snelkoppeling bureaublad", "Snelkoppeling bureaublad ✓", "Aan taakbalk vastmaken",
        "Vastgemaakt aan taakbalk ✓",
        "Zet een snelkoppeling naar deze kopie van het programma op het bureaublad. Een snelkoppeling naar een verplaatste of verwijderde kopie wordt vervangen.",
        "Windows laat het vastmaken aan jou over. Dit bereidt alles voor en toont de ene klik die nodig is.",
        "De vermelding in het Startmenu kon niet worden gemaakt.",
        "Windows laat alleen jou programma's vastmaken, dus het kost één klik van jou: klik met rechts op het pictogram van dit programma in de taakbalk en kies ‘Aan taakbalk vastmaken’. De benodigde vermelding in het Startmenu staat klaar, en deze knop toont een vinkje zodra het vastgemaakt is."),
 "pl": ("Skrót na pulpicie", "Skrót na pulpicie ✓", "Przypnij do paska zadań",
        "Przypięto do paska zadań ✓",
        "Umieszcza na pulpicie skrót do tej kopii programu. Skrót wskazujący przeniesioną lub usuniętą kopię zostaje zastąpiony.",
        "Windows zostawia przypinanie tobie. To przygotowuje wszystko i pokazuje jedno potrzebne kliknięcie.",
        "Nie udało się utworzyć wpisu w menu Start.",
        "Windows pozwala przypinać programy tylko tobie, więc potrzebne jest jedno twoje kliknięcie: kliknij prawym przyciskiem ikonę tego programu na pasku zadań i wybierz „Przypnij do paska zadań”. Potrzebny wpis w menu Start już jest, a ten przycisk pokaże znacznik, gdy przypięcie będzie gotowe."),
 "sv": ("Genväg på skrivbordet", "Genväg på skrivbordet ✓", "Fäst i aktivitetsfältet",
        "Fäst i aktivitetsfältet ✓",
        "Lägger en genväg till den här kopian av programmet på skrivbordet. En genväg till en flyttad eller borttagen kopia ersätts.",
        "Windows låter dig själv fästa. Det här förbereder allt och visar det enda klick som behövs.",
        "Startmenyposten kunde inte skapas.",
        "Windows låter bara dig själv fästa program, så det krävs ett klick av dig: högerklicka på programmets ikon i aktivitetsfältet och välj ”Fäst i aktivitetsfältet”. Startmenyposten som behövs finns på plats, och knappen visar en bock så fort programmet är fäst."),
 "da": ("Genvej på skrivebordet", "Genvej på skrivebordet ✓", "Fastgør til proceslinjen",
        "Fastgjort til proceslinjen ✓",
        "Lægger en genvej til denne kopi af programmet på skrivebordet. En genvej til en flyttet eller slettet kopi bliver erstattet.",
        "Windows overlader fastgørelsen til dig. Dette forbereder alt og viser det ene klik, der skal til.",
        "Posten i menuen Start kunne ikke oprettes.",
        "Windows lader kun dig selv fastgøre programmer, så det kræver et klik fra dig: højreklik på programmets ikon på proceslinjen, og vælg »Fastgør til proceslinjen«. Den nødvendige post i menuen Start er på plads, og knappen viser et flueben, så snart det er fastgjort."),
 "fi": ("Työpöydän pikakuvake", "Työpöydän pikakuvake ✓", "Kiinnitä tehtäväpalkkiin",
        "Kiinnitetty tehtäväpalkkiin ✓",
        "Lisää työpöydälle pikakuvakkeen tähän ohjelman kopioon. Siirrettyyn tai poistettuun kopioon osoittava pikakuvake korvataan.",
        "Windows jättää kiinnittämisen sinulle. Tämä valmistelee kaiken ja näyttää sen yhden tarvittavan napsautuksen.",
        "Käynnistä-valikon kohdetta ei voitu luoda.",
        "Windows antaa vain sinun itse kiinnittää ohjelmia, joten tarvitaan yksi napsautus sinulta: napsauta tämän ohjelman kuvaketta tehtäväpalkissa hiiren kakkospainikkeella ja valitse ”Kiinnitä tehtäväpalkkiin”. Tarvittava Käynnistä-valikon kohde on valmiina, ja tämä painike näyttää valintamerkin heti, kun kiinnitys on tehty."),
 "cs": ("Zástupce na ploše", "Zástupce na ploše ✓", "Připnout na hlavní panel",
        "Připnuto na hlavní panel ✓",
        "Umístí na plochu zástupce této kopie programu. Zástupce, který ukazuje na přesunutou nebo smazanou kopii, se nahradí.",
        "Připnutí nechává Windows na tobě. Tohle vše připraví a ukáže to jediné potřebné kliknutí.",
        "Položku v nabídce Start se nepodařilo vytvořit.",
        "Windows dovoluje připínat programy jen tobě, takže je potřeba jedno tvoje kliknutí: klikni pravým tlačítkem na ikonu tohoto programu na hlavním panelu a zvol „Připnout na hlavní panel“. Potřebná položka v nabídce Start je připravená a toto tlačítko ukáže zaškrtnutí, jakmile bude program připnutý."),
 "hu": ("Asztali parancsikon", "Asztali parancsikon ✓", "Rögzítés a tálcán",
        "Rögzítve a tálcán ✓",
        "Parancsikont tesz az asztalra a program ezen példányához. Az áthelyezett vagy törölt példányra mutatót lecseréli.",
        "A rögzítést a Windows rád bízza. Ez mindent előkészít, és megmutatja az egyetlen szükséges kattintást.",
        "A Start menü bejegyzést nem sikerült létrehozni.",
        "A Windows csak neked engedi a programok rögzítését, ezért egy kattintás kell tőled: kattints jobb gombbal a program ikonjára a tálcán, és válaszd a „Rögzítés a tálcán” lehetőséget. A szükséges Start menü bejegyzés elkészült, és ez a gomb pipát mutat, amint a rögzítés megtörtént."),
 "ro": ("Comandă rapidă pe desktop", "Comandă rapidă pe desktop ✓",
        "Fixare în bara de activități", "Fixat în bara de activități ✓",
        "Pune pe desktop o comandă rapidă către această copie a programului. Una care indică o copie mutată sau ștearsă este înlocuită.",
        "Windows îți lasă ție fixarea. Aceasta pregătește totul și îți arată singurul clic necesar.",
        "Intrarea din meniul Start nu a putut fi creată.",
        "Windows te lasă doar pe tine să fixezi programe, deci e nevoie de un clic de la tine: clic dreapta pe pictograma acestui program din bara de activități și alege „Fixare în bara de activități”. Intrarea necesară din meniul Start există, iar acest buton arată o bifă imediat ce programul e fixat."),
 "el": ("Στην επιφάνεια εργασίας", "Στην επιφάνεια εργασίας ✓",
        "Καρφίτσωμα στη γραμμή εργασιών", "Στη γραμμή εργασιών ✓",
        "Βάζει στην επιφάνεια εργασίας μια συντόμευση προς αυτό το αντίγραφο του προγράμματος. Μια συντόμευση προς αντίγραφο που μετακινήθηκε ή διαγράφηκε αντικαθίσταται.",
        "Τα Windows αφήνουν το καρφίτσωμα σε εσένα. Αυτό προετοιμάζει τα πάντα και δείχνει το ένα κλικ που χρειάζεται.",
        "Δεν ήταν δυνατή η δημιουργία της καταχώρισης στο μενού Έναρξη.",
        "Τα Windows αφήνουν μόνο εσένα να καρφιτσώνεις προγράμματα, οπότε χρειάζεται ένα δικό σου κλικ: δεξί κλικ στο εικονίδιο αυτού του προγράμματος στη γραμμή εργασιών και επίλεξε «Καρφίτσωμα στη γραμμή εργασιών». Η απαραίτητη καταχώριση στο μενού Έναρξη υπάρχει ήδη, και αυτό το κουμπί δείχνει ένα τικ μόλις γίνει το καρφίτσωμα."),
 "ru": ("Ярлык на рабочем столе", "Ярлык на рабочем столе ✓", "Закрепить на панели задач",
        "Закреплено на панели задач ✓",
        "Помещает на рабочий стол ярлык этой копии программы. Ярлык, указывающий на перемещённую или удалённую копию, заменяется.",
        "Закрепление Windows оставляет тебе. Эта кнопка всё подготовит и покажет единственный нужный щелчок.",
        "Не удалось создать пункт в меню «Пуск».",
        "Windows разрешает закреплять программы только тебе самому, поэтому нужен один твой щелчок: щёлкни правой кнопкой по значку этой программы на панели задач и выбери «Закрепить на панели задач». Нужный пункт в меню «Пуск» уже создан, а эта кнопка покажет галочку, как только программа будет закреплена."),
 "tr": ("Masaüstü kısayolu", "Masaüstü kısayolu ✓", "Görev çubuğuna sabitle",
        "Görev çubuğuna sabitlendi ✓",
        "Programın bu kopyasına bir kısayolu masaüstüne koyar. Taşınmış veya silinmiş bir kopyayı gösteren kısayol değiştirilir.",
        "Windows sabitlemeyi sana bırakır. Bu her şeyi hazırlar ve gereken tek tıklamayı gösterir.",
        "Başlat menüsü girdisi oluşturulamadı.",
        "Windows programları yalnızca senin sabitlemene izin verir, bu yüzden senden bir tık gerekiyor: görev çubuğundaki bu programın simgesine sağ tıkla ve “Görev çubuğuna sabitle” seçeneğini seç. Gereken Başlat menüsü girdisi hazır; sabitleme yapılır yapılmaz bu düğme bir onay işareti gösterir."),
 "id": ("Pintasan desktop", "Pintasan desktop ✓", "Sematkan ke bilah tugas",
        "Tersemat di bilah tugas ✓",
        "Menaruh pintasan ke salinan program ini di desktop. Pintasan yang mengarah ke salinan yang dipindahkan atau dihapus akan diganti.",
        "Windows menyerahkan penyematan kepadamu. Ini menyiapkan semuanya dan menunjukkan satu klik yang diperlukan.",
        "Entri menu Mulai tidak dapat dibuat.",
        "Windows hanya mengizinkan kamu sendiri menyematkan program, jadi perlu satu klik darimu: klik kanan ikon program ini di bilah tugas lalu pilih “Sematkan ke bilah tugas”. Entri menu Mulai yang diperlukan sudah ada, dan tombol ini menampilkan tanda centang begitu program tersemat."),
 "ms": ("Pintasan desktop", "Pintasan desktop ✓", "Pin ke bar tugas", "Dipin ke bar tugas ✓",
        "Meletakkan pintasan ke salinan program ini pada desktop. Pintasan yang menghala ke salinan yang dialihkan atau dipadam akan diganti.",
        "Windows menyerahkan penyematan kepada anda. Ini menyediakan semuanya dan menunjukkan satu klik yang diperlukan.",
        "Entri menu Mula tidak dapat dicipta.",
        "Windows hanya membenarkan anda sendiri menyemat program, jadi satu klik daripada anda diperlukan: klik kanan ikon program ini pada bar tugas dan pilih “Pin ke bar tugas”. Entri menu Mula yang diperlukan sudah tersedia, dan butang ini menunjukkan tanda semak sebaik sahaja ia disemat."),
 "vi": ("Lối tắt trên màn hình nền", "Lối tắt trên màn hình nền ✓", "Ghim vào thanh tác vụ",
        "Đã ghim vào thanh tác vụ ✓",
        "Đặt lối tắt tới bản sao này của chương trình lên màn hình nền. Lối tắt trỏ tới một bản sao đã bị di chuyển hoặc xóa sẽ được thay thế.",
        "Windows để việc ghim cho bạn. Nút này chuẩn bị mọi thứ và chỉ cho bạn cú nhấp duy nhất cần làm.",
        "Không thể tạo mục trong menu Start.",
        "Windows chỉ cho phép chính bạn ghim chương trình, nên cần một cú nhấp của bạn: nhấp chuột phải vào biểu tượng của chương trình này trên thanh tác vụ và chọn “Ghim vào thanh tác vụ”. Mục cần thiết trong menu Start đã được tạo, và nút này sẽ hiện dấu tích ngay khi chương trình được ghim."),
 "th": ("ทางลัดบนเดสก์ท็อป", "ทางลัดบนเดสก์ท็อป ✓", "ปักหมุดที่แถบงาน", "ปักหมุดที่แถบงานแล้ว ✓",
        "วางทางลัดไปยังสำเนาโปรแกรมนี้บนเดสก์ท็อป ทางลัดที่ชี้ไปยังสำเนาที่ถูกย้ายหรือลบแล้วจะถูกแทนที่",
        "Windows ให้คุณปักหมุดเอง ปุ่มนี้เตรียมทุกอย่างไว้และบอกการคลิกเพียงครั้งเดียวที่ต้องทำ",
        "ไม่สามารถสร้างรายการในเมนูเริ่มได้",
        "Windows ให้คุณปักหมุดโปรแกรมได้ด้วยตัวเองเท่านั้น จึงต้องใช้การคลิกจากคุณหนึ่งครั้ง: คลิกขวาที่ไอคอนของโปรแกรมนี้บนแถบงาน แล้วเลือก “ปักหมุดที่แถบงาน” รายการในเมนูเริ่มที่จำเป็นพร้อมแล้ว และปุ่มนี้จะแสดงเครื่องหมายถูกทันทีที่ปักหมุดเสร็จ"),
 "ja": ("デスクトップにショートカット", "デスクトップにショートカット ✓", "タスク バーにピン留め",
        "タスク バーにピン留め済み ✓",
        "このプログラムのこのコピーへのショートカットをデスクトップに作成します。移動または削除されたコピーを指すショートカットは置き換えられます。",
        "ピン留めは Windows によりユーザーに任されています。このボタンは準備をすべて済ませ、必要な 1 回のクリックを案内します。",
        "スタート メニューの項目を作成できませんでした。",
        "Windows ではプログラムのピン留めはユーザー自身にしかできないため、クリックが 1 回必要です。タスク バーにあるこのプログラムのアイコンを右クリックし、「タスク バーにピン留めする」を選んでください。必要なスタート メニューの項目は作成済みで、ピン留めされるとこのボタンにチェック マークが表示されます。"),
 "ko": ("바탕 화면 바로 가기", "바탕 화면 바로 가기 ✓", "작업 표시줄에 고정", "작업 표시줄에 고정됨 ✓",
        "이 프로그램 사본의 바로 가기를 바탕 화면에 만듭니다. 이동되거나 삭제된 사본을 가리키는 바로 가기는 교체됩니다.",
        "Windows는 고정을 사용자에게 맡깁니다. 이 버튼이 모든 준비를 마치고 필요한 한 번의 클릭을 알려 줍니다.",
        "시작 메뉴 항목을 만들 수 없습니다.",
        "Windows에서는 사용자만 프로그램을 고정할 수 있으므로 한 번의 클릭이 필요합니다. 작업 표시줄에서 이 프로그램 아이콘을 마우스 오른쪽 단추로 클릭하고 “작업 표시줄에 고정”을 선택하세요. 필요한 시작 메뉴 항목은 이미 만들어졌으며, 고정되면 이 버튼에 체크 표시가 나타납니다."),
 "zh-Hans": ("桌面快捷方式", "桌面快捷方式 ✓", "固定到任务栏", "已固定到任务栏 ✓",
             "在桌面上创建指向本程序这一副本的快捷方式。指向已移动或已删除副本的快捷方式会被替换。",
             "Windows 把固定操作留给你自己。此按钮会准备好一切，并告诉你还需要点的那一下。",
             "无法创建开始菜单项。",
             "Windows 只允许你自己固定程序，所以需要你点一下：在任务栏中右键单击本程序的图标，然后选择“固定到任务栏”。所需的开始菜单项已经就绪，固定完成后此按钮会显示对勾。"),
 "zh-Hant": ("桌面捷徑", "桌面捷徑 ✓", "釘選到工作列", "已釘選到工作列 ✓",
             "在桌面上建立指向本程式這個副本的捷徑。指向已移動或已刪除副本的捷徑會被取代。",
             "Windows 把釘選留給你自己操作。此按鈕會準備好一切，並告訴你還需要按的那一下。",
             "無法建立開始功能表項目。",
             "Windows 只允許你自己釘選程式，所以需要你點一下：在工作列中以滑鼠右鍵按一下本程式的圖示，然後選擇「釘選到工作列」。所需的開始功能表項目已經就緒，釘選完成後此按鈕會顯示勾號。"),
}

_KEYS = (A, B, C, D, E, F, G, H)
for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(_KEYS), (_code, len(_saetze))
    # Das Haekchen gehoert in die Erledigt-Fassung JEDER Sprache, sonst sieht der
    # Knopf in dieser Sprache nach dem Anlegen genauso aus wie davor.
    assert _saetze[1].endswith("✓") and _saetze[3].endswith("✓"), _code

ZUSATZ = {code: dict(zip(_KEYS, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
