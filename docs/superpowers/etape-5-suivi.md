# Étape 5 — décisions prises et points à reprendre

L'utilisateur a demandé d'avancer sans questions (« go for it ») : le plan n'a pas attendu sa relecture, et les choix restants sont ceux de Claude. Ils sont marqués *(Claude)* dans `specs/2026-11-12-mise-en-production-design.md` lorsqu'ils y figurent.

## Décisions prises en cours de route

- **Inscription.** Elle reste ouverte tant qu'aucun compte n'existe, ou quand `Accounts:AllowRegistration` est activé. Les nouvelles inscriptions passent ensuite par ce réglage, y compris via un fournisseur externe.
- **E-mails.**
  - Une panne du serveur d'envoi ne compte pas comme un essai : l'envoi est retenté toutes les 30 secondes, et abandonné après 24 heures.
  - Le lien de réservation d'un e-mail fonctionne même si la vitrine n'est pas publiée ou si le slug a changé : le jeton suffit à retrouver la réservation.
  - Un paiement reçu après l'annulation du client est remboursé, sans e-mail (ce cas n'était pas prévu dans la spec).
- **Envoi des e-mails : adresse d'expédition refusée.** Si le serveur d'envoi refuse l'adresse d'expédition de la plateforme, les e-mails ne sont plus abandonnés : ils attendent que l'adresse soit corrigée et partent ensuite, dans la limite de 24 heures. Un destinataire refusé reste abandonné tout de suite. Chaque abandon est écrit en erreur dans les journaux. De plus, `Email:FromAddress` doit être une adresse valide en production.
- **Réglage `Log` pour les e-mails.** Il n'écrit pas le lien de confirmation dans les journaux (le contenu des e-mails contient des données personnelles). Hors démonstration, le site refuse donc de démarrer avec `Log` tant que la confirmation des comptes est exigée ; `Log` n'est accepté que pour une démo hébergée (`DEMO_SEED_ON_STARTUP=true`) ou si la confirmation est désactivée.
- **Connexion.** Un seul message d'échec, qui mentionne aussi l'adresse « pas encore confirmée » : rien ne révèle qu'un compte existe, et le chef non confirmé comprend quoi faire.
- **Fuseau horaire.** L'entrée de prix d'un ticket sans date est écrite au jour local de sa création, comme une étiquette, et non à l'instant brut.
- **Renommage d'un service.** Il prend un verrou pour chaque date locale de ce jour de semaine, entre aujourd'hui et l'horizon des réservations en ligne, avant de relire les réservations à venir. Une réservation manuelle plus lointaine et simultanée reste hors périmètre (très improbable).
- **Version d'édition.** Les éditions de commande et de ticket reçoivent la version lue (`expectedVersion`, nullable) : seuls les tests anciens passent `null`. Une commande ne peut plus être éditée dès que la cuisine a fait avancer son statut : c'est voulu, à signaler lors d'une démonstration.
- **Migrations.** La commande de migration du plan ne fonctionne pas telle quelle (le back n'a pas les outils EF) : on utilise celle du README, `dotnet ef migrations add <Nom> -p src/ChefOS.Infrastructure -o Data/Migrations`.
- **Messages d'erreur.** Un site mal configuré se refuse à démarrer avec « Configuration de production incomplète », qui dit quoi régler sans jamais citer une valeur secrète. Le mode sans chiffrement des e-mails (`Smtp:Security=None`) est refusé hors développement.
- **Images et téléchargements.** Les images Docker et les paquets NuGet ajoutés (MailKit, clés de protection des données en base, contrôles de santé) sont traités comme l'installation normale des dépendances.
- **Déploiement.**
  - Les conteneurs n'écoutent qu'en HTTP sur 8080, derrière Caddy ; les en-têtes `X-Forwarded-*` ne sont acceptés que du sous-réseau Docker fixé dans la composition.
  - Aucun site ni la base ne publie de port sur le serveur : seul Caddy ouvre 80 et 443. Sinon le proxy utilisateur de Docker ferait passer tous les visiteurs pour la passerelle du réseau, qui est dans le réseau de confiance.
  - Chaque site n'accepte que son propre domaine (`AllowedHosts`), plus `localhost` pour la sonde de santé du conteneur.
  - Les images installent `curl` pour la sonde de santé, l'image d'exécution n'en contenant pas.
  - Le back a une période de démarrage de 120 secondes (il migre la base avant d'écouter) ; la vitrine attend que le back soit prêt.

## À reprendre

- **Configuration de production.**
  - Un site en production derrière Caddy sans `ReverseProxy:KnownNetworks` échoue en silence (le site croit être en HTTP, les cookies ne sont pas `Secure`, les liens des e-mails sont internes) : ajouter un avertissement au démarrage ou une règle de configuration. Le réseau `0.0.0.0/0` est aujourd'hui accepté.
  - Comparer `Smtp:Security` à des constantes plutôt qu'à des chaînes.
- **Clés de protection des données.**
  - Elles sont stockées en clair en base : prévoir `ProtectKeysWithCertificate`.
  - Une ligne de clé XML malformée casse tout : ajouter une garde dans la lecture des clés.
  - Tests manquants : jeton d'une instance accepté par une autre, isolation par nom d'application, valeur par défaut de `Database:MigrateOnStartup`, graine de démo en production sans mot de passe.
  - Le slug « healthz » est masqué par la route de santé.
- **E-mails.**
  - Une écriture « envoyé » qui échoue après l'envoi peut provoquer un doublon, et un processus figé plus de deux minutes peut écraser un envoi réussi par « abandonné » (au-moins-une-fois accepté).
  - La durée maximale de 30 secondes d'un envoi dépend du comportement du transport ; le bail suppose que l'horloge est synchronisée.
  - `Enqueue=false` est ignoré en silence ; le texte « Bienvenue… activer votre compte » est aussi envoyé pour un simple changement d'adresse.
  - Une course est possible sur la création du tout premier compte.
  - Les pages de compte qui envoient un e-mail (renvoi de confirmation, mot de passe oublié, changement d'adresse) ne sont pas encore limitées en nombre de demandes : reporté.
- **Fuseau horaire et prix.**
  - Le fuseau du restaurant est gardé en cache pour la durée du circuit : un changement n'apparaît qu'au prochain circuit.
  - L'historique des prix mélange étiquettes (tickets) et instants (saisies manuelles) dans une même colonne : le dernier prix affiché peut être le mauvais dans de rares cas de minuit. La correction propre est une colonne « jour » séparée.
  - Les sélecteurs de date ne limitent pas la date avant le chargement du calendrier ; la liste des pertes montre brièvement des dates vides.
  - Un nom de fuseau (« Europe/Brussels ») est répété à plusieurs endroits.
- **Réservations.**
  - Un service qui a un paiement périmé en attente est compté comme actif ; fermer un jour déjà fermé demande confirmation puis échoue.
  - La lecture du nombre de réservations actives peut créer un profil par défaut.
  - Plusieurs formulations à revoir (« contactez le restaurant » sans contact publié).
- **Comptes.** Ouvrir les pages « codes de récupération » ou « désactiver la double authentification » sans double authentification active donne une erreur 500 (comportement d'Identity préexistant) : rediriger avec un message. Quelques libellés restent incohérents, et deux titres de page n'ont pas « — ChefOS ».
- **Édition de commande ou de ticket.** Rendre `expectedVersion` obligatoire plutôt que facultatif ; l'ordre des messages d'erreur à l'enregistrement d'un brouillon de ticket est à revoir.
- **Tests.**
  - La page d'erreur de composant et le dialogue de rappel de TVA n'ont pas de test automatisé (pas de bUnit).
  - Plusieurs tests sont lents ou à délai fixe (verrou de renommage, ~4 s) ; l'idempotence des paiements n'est testée qu'en séquentiel.
