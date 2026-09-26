TRON-Auto-Sweeper — Windows

Что делает:
- мониторит входящие native TRX на адресе, полученном из приватного ключа;
- после нового подтвержденного входящего TRX рассчитывает баланс минус резерв;
- создаёт native TRX TransferContract;
- подписывает транзакцию локально приватным ключом через TronNet;
- отправляет подписанную транзакцию в TRON;
- хранит приватный ключ локально через Windows DPAPI.

ВАЖНО:
1. Не вводите приватный ключ в чат и не передавайте его другим людям.
2. Это финансовая автоматизация. Сначала используйте отдельный тестовый/малобалансовый кошелёк.
3. Проект использует TronNet 0.2.0. Пакет старый, поэтому перед использованием на значимых средствах проведите независимую проверку исходников/зависимостей.
4. TRON официально требует: создать unsigned transaction, подписать локально, затем broadcast. Broadcast сам по себе не является подтверждением; окончательный статус нужно проверять через solidified chain.
5. Текущая версия уже не содержит намеренно отключённой подписи из предыдущего scaffold, но перед реальным mainnet использованием всё равно нужна проверка сборки и тестовая транзакция.

Сборка без установки .NET на вашем ПК:
- Загрузите папку проекта в GitHub.
- В Actions запустите workflow "Build Windows EXE".
- GitHub Windows runner соберёт self-contained TRON-Auto-Sweeper.exe.

Локальная сборка (если .NET 8 SDK установлен):
dotnet publish TRON-Auto-Sweeper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
