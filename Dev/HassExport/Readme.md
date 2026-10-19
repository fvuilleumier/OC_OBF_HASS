# Premier lancement

dotnet build
pwsh bin/Debug/net10.0/playwright.ps1 install chromium
dotnet run

Au premier lancement (Headless: false), Chromium s'ouvre : connecte-toi à HA. Le script attend jusqu'à 5 minutes, puis génère les fichiers.

# Ensuite 
Ta session est enregistrée dans .playwright-profile. Les fois suivantes, tu peux passer Headless à true.