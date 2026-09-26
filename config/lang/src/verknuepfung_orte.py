# -*- coding: utf-8 -*-
"""Die drei neuen Saetze der Verknuepfungsfrage, in allen 25 Sprachen.

Seit dem 2026-09-16 bietet das Zustimmungsfenster nicht nur den Schreibtisch an,
sondern auch das Startmenue und den Autostart -- und erklaert, warum die
Taskleiste nicht dabei ist (Windows laesst das Anheften nur von Hand zu, siehe
Shortcuts.cs).

ZUSATZ-Modul nach Schluessel, wie `verknuepfung.py`. Aufbau dort erklaert.

Diese Uebersetzungen sind nicht von Muttersprachlern geprueft.
"""

STARTMENUE = "Add it to the Start menu"
AUTOSTART = "Start it when I sign in to Windows"
TASKLEISTE = ("Windows only lets you pin to the taskbar yourself: right-click "
              "the Start menu entry and choose “Pin to taskbar”.")

_UEBERSETZT = {
    "de": ("Im Startmenü eintragen",
           "Beim Anmelden bei Windows mitstarten",
           "Das Anheften an die Taskleiste lässt Windows nur von Hand zu: "
           "Rechtsklick auf den Startmenü-Eintrag, dann „An Taskleiste anheften“."),
    "fr": ("L'ajouter au menu Démarrer",
           "Le lancer à l'ouverture de session Windows",
           "Windows ne permet d'épingler à la barre des tâches que manuellement : "
           "clic droit sur l'entrée du menu Démarrer, puis « Épingler à la barre "
           "des tâches »."),
    "es": ("Añadirlo al menú Inicio",
           "Iniciarlo al iniciar sesión en Windows",
           "Windows solo permite anclar a la barra de tareas manualmente: haz clic "
           "derecho en la entrada del menú Inicio y elige «Anclar a la barra de "
           "tareas»."),
    "it": ("Aggiungerlo al menu Start",
           "Avviarlo all'accesso a Windows",
           "Windows consente di aggiungere alla barra delle applicazioni solo "
           "manualmente: fai clic destro sulla voce del menu Start e scegli "
           "«Aggiungi alla barra delle applicazioni»."),
    "pt": ("Adicioná-lo ao menu Iniciar",
           "Iniciá-lo ao entrar no Windows",
           "O Windows só permite fixar na barra de tarefas manualmente: clique com "
           "o botão direito na entrada do menu Iniciar e escolha «Fixar na barra "
           "de tarefas»."),
    "nl": ("Toevoegen aan het Startmenu",
           "Starten wanneer ik me bij Windows aanmeld",
           "Windows laat vastmaken aan de taakbalk alleen handmatig toe: "
           "rechtsklik op het item in het Startmenu en kies "
           "‘Aan taakbalk vastmaken’."),
    "pl": ("Dodaj do menu Start",
           "Uruchamiaj po zalogowaniu do systemu Windows",
           "Windows pozwala przypiąć do paska zadań tylko ręcznie: kliknij prawym "
           "przyciskiem wpis w menu Start i wybierz „Przypnij do paska zadań”."),
    "sv": ("Lägg till i Start-menyn",
           "Starta när jag loggar in i Windows",
           "Windows tillåter bara att du fäster i aktivitetsfältet själv: "
           "högerklicka på posten i Start-menyn och välj ”Fäst i aktivitetsfältet”."),
    "da": ("Føj til Start-menuen",
           "Start den, når jeg logger på Windows",
           "Windows tillader kun, at du selv fastgør til proceslinjen: højreklik "
           "på punktet i Start-menuen og vælg ”Fastgør til proceslinjen”."),
    "fi": ("Lisää Käynnistä-valikkoon",
           "Käynnistä, kun kirjaudun Windowsiin",
           "Windows sallii tehtäväpalkkiin kiinnittämisen vain käsin: napsauta "
           "Käynnistä-valikon kohtaa hiiren oikealla painikkeella ja valitse "
           "”Kiinnitä tehtäväpalkkiin”."),
    "cs": ("Přidat do nabídky Start",
           "Spouštět při přihlášení k Windows",
           "Windows umožňuje připnout na hlavní panel jen ručně: klikněte pravým "
           "tlačítkem na položku v nabídce Start a zvolte „Připnout na hlavní "
           "panel“."),
    "hu": ("Hozzáadás a Start menühöz",
           "Induljon el, amikor bejelentkezem a Windowsba",
           "A Windows csak kézzel engedi a tálcához rögzítést: kattintson jobb "
           "gombbal a Start menü bejegyzésére, és válassza a „Rögzítés a tálcán” "
           "lehetőséget."),
    "ro": ("Adaugă-l în meniul Start",
           "Pornește-l când mă conectez la Windows",
           "Windows permite fixarea în bara de activități doar manual: clic dreapta "
           "pe intrarea din meniul Start și alege „Fixare în bara de activități”."),
    "el": ("Προσθήκη στο μενού Έναρξη",
           "Εκκίνηση κατά τη σύνδεση στα Windows",
           "Τα Windows επιτρέπουν το καρφίτσωμα στη γραμμή εργασιών μόνο "
           "χειροκίνητα: κάντε δεξί κλικ στην καταχώριση του μενού Έναρξη και "
           "επιλέξτε «Καρφίτσωμα στη γραμμή εργασιών»."),
    "ru": ("Добавить в меню «Пуск»",
           "Запускать при входе в Windows",
           "Windows разрешает закрепление на панели задач только вручную: щёлкните "
           "правой кнопкой по пункту в меню «Пуск» и выберите «Закрепить на панели "
           "задач»."),
    "tr": ("Başlat menüsüne ekle",
           "Windows'ta oturum açtığımda başlat",
           "Windows görev çubuğuna sabitlemeye yalnızca elle izin verir: Başlat "
           "menüsündeki girdiye sağ tıklayın ve “Görev çubuğuna sabitle” seçeneğini "
           "seçin."),
    "id": ("Tambahkan ke menu Start",
           "Jalankan saat saya masuk ke Windows",
           "Windows hanya mengizinkan Anda menyematkan ke taskbar sendiri: klik "
           "kanan entri menu Start lalu pilih “Sematkan ke taskbar”."),
    "ms": ("Tambahkan pada menu Mula",
           "Mulakan apabila saya log masuk ke Windows",
           "Windows hanya membenarkan anda menyemat pada bar tugas sendiri: klik "
           "kanan entri menu Mula dan pilih “Semat pada bar tugas”."),
    "vi": ("Thêm vào menu Start",
           "Khởi động khi tôi đăng nhập Windows",
           "Windows chỉ cho phép bạn tự ghim vào thanh tác vụ: bấm chuột phải vào "
           "mục trong menu Start rồi chọn “Ghim vào thanh tác vụ”."),
    "th": ("เพิ่มลงในเมนู Start",
           "เริ่มโปรแกรมเมื่อฉันลงชื่อเข้าใช้ Windows",
           "Windows อนุญาตให้ปักหมุดบนแถบงานด้วยตนเองเท่านั้น: คลิกขวาที่รายการใน"
           "เมนู Start แล้วเลือก “ปักหมุดที่แถบงาน”"),
    "ja": ("スタートメニューに追加する",
           "Windows へのサインイン時に起動する",
           "タスクバーへのピン留めは Windows では手動でしかできません。スタート"
           "メニューの項目を右クリックし、「タスクバーにピン留めする」を選んで"
           "ください。"),
    "ko": ("시작 메뉴에 추가",
           "Windows에 로그인할 때 시작",
           "작업 표시줄 고정은 Windows에서 직접 하는 것만 허용됩니다. 시작 메뉴 "
           "항목을 마우스 오른쪽 버튼으로 클릭한 뒤 “작업 표시줄에 고정”"
           "을 선택하세요."),
    "zh-Hans": ("添加到开始菜单",
                "登录 Windows 时启动",
                "Windows 只允许你自己固定到任务栏：右键单击开始菜单中的项目，"
                "然后选择“固定到任务栏”。"),
    "zh-Hant": ("新增到開始功能表",
                "登入 Windows 時啟動",
                "Windows 只允許你自己釘選到工作列：在開始功能表的項目上按右鍵，"
                "然後選擇「釘選到工作列」。"),
}
_UEBERSETZT["zh"] = _UEBERSETZT["zh-Hans"]

ZUSATZ = {code: {STARTMENUE: a, AUTOSTART: b, TASKLEISTE: c}
          for code, (a, b, c) in _UEBERSETZT.items()}
