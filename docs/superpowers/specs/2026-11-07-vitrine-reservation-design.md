# ChefOS — Étape 4 : Vitrine, carte des vins, réservation et prépaiement

**Date** : 2026-11-07
**Statut** : le parcours client a été validé par l'utilisateur (maquette), ainsi que la bibliothèque de vins avec brouillon de fiche assisté. L'utilisateur a ensuite demandé de passer au code (« je te fais confiance »). **Les choix marqués *(Claude)* sont donc les miens.**
**Base** : branche `feature/etape-4-vitrine`, créée depuis `feature/etape-3-ventes-stats`.

## 1. Intention (dans les mots de l'utilisateur)

Sur son téléphone, le client découvre d'abord **l'ambiance et le thème** du restaurant, « puis paf, le menu, car c'est tout ce qu'on veut ». Il peut ensuite consulter **la carte des vins en avance**, présentée comme une bibliothèque avec la description complète de chaque vin. En bas de page, un bouton **Réserver** lui permet de réserver X places et de payer un acompte :
- **au-delà de 5 personnes**, il réserve aussi **le plat de chaque convive**, avec en option le prénom et le nom de chacun pour s'y retrouver sur place ;
- il peut **prépayer une partie du repas**, selon ce que le restaurateur a choisi dans ses paramètres.

**Anti-objectifs**
- On ne vend jamais deux fois la même place.
- On ne débite jamais le client sans confirmation claire du montant et de la règle d'annulation.
- Aucune fiche de vin rédigée par l'IA n'est publiée sans validation du restaurateur.
- Un restaurant ne voit jamais les données d'un autre, et la vitrine n'expose que ce qui est publié.

## 2. Site public (`ChefOS.Vitrine`, URL `/{slug}`)

1. **Ambiance**
   - photo de couverture (ou dégradé de repli), nom, ville ;
   - thème en une phrase (« Cuisine belge de saison, bières d'abbaye, feu de bois ») ;
   - un texte d'ambiance de quelques lignes et une galerie de 0 à 6 photos.
2. **Menu**, juste après :
   - les plats publiés, groupés par catégorie, avec nom, description publique, prix TTC et allergènes (icônes et libellés) ;
   - les plats archivés ou non publiés n'apparaissent pas.
3. **Carte des vins** :
   - aperçu de 3 vins, puis lien vers la bibliothèque complète `/{slug}/vins`, avec filtres par couleur et par région ;
   - fiche `/{slug}/vins/{id}` : robe, nez, bouche, garde, température de service, accords (plats du restaurant liés et suggestions générales), mot du restaurateur, prix bouteille et verre.
4. **Réservation** : bouton en bas de page (et bouton flottant discret sur mobile) qui mène à `/{slug}/reserver`.

Une vitrine non publiée renvoie 404. Les pages publiques n'exigent aucun compte.

## 3. Réservation *(règles : Claude)*

- **Capacité par service** :
  - le restaurateur définit ses services par jour de semaine (par exemple « Midi » 12:00–14:00 et « Soir » 19:00–22:00) avec un nombre maximum de couverts et un pas de créneau (30 min par défaut), plus des jours de fermeture exceptionnelle ;
  - places disponibles d'un service = capacité − couverts déjà réservés (réservations confirmées, et réservations en attente de paiement de moins de 30 min).
- **Heures** :
  - les heures sont exprimées dans le fuseau du restaurant (`Restaurant.TimeZoneId`, par défaut `Europe/Brussels`) et stockées en UTC ;
  - on ne peut réserver qu'à partir de maintenant + 2 h (réglable) et jusqu'à 60 jours à l'avance (réglable).
- **Taille du groupe** : de 1 au maximum réglable (20 par défaut).
- **Petit groupe** (≤ seuil, 5 par défaut, réglable) :
  - on saisit date, créneau, nombre de couverts, coordonnées (nom, e-mail, téléphone) et une remarque (allergies, occasion) ;
  - **acompte** = acompte par personne × couverts (10 € par défaut, 0 = sans acompte).
- **Grand groupe** (> seuil) :
  - en plus, une ligne par convive avec le prénom et le nom en option, et au moins un plat choisi dans le menu publié (plusieurs possibles : entrée, plat, dessert) ;
  - **prépaiement** = pourcentage réglable (30 % par défaut) du total TTC des plats pré-commandés ;
  - l'acompte par personne ne s'ajoute pas au prépaiement.
- **Paiement** :
  - via la passerelle configurée ;
  - la réservation passe de « En attente de paiement » à « Confirmée » quand le paiement est confirmé, et n'importe quel montant à 0 confirme directement ;
  - un paiement échoué, expiré ou annulé libère la place.
- **Annulation par le client** : un lien personnel (jeton) est affiché après la réservation.
  - Jusqu'à 24 h avant (réglable), l'annulation est gratuite. Le remboursement est marqué « À rembourser » et le restaurateur le déclenche en un clic, ou il part automatiquement si la passerelle le permet (choix : automatique via la passerelle).
  - Après ce délai, la réservation est annulée sans remboursement, et la règle est affichée avant le paiement.
- **Côté restaurateur** (`/reservations` dans le back) :
  - vue par jour et par service : couverts réservés / capacité, liste avec convives et plats pré-commandés ;
  - statuts : Arrivé, No-show, Annuler (avec remboursement si payé) ;
  - création manuelle, pour une réservation prise au téléphone, sans paiement.
  - « Arrivé » crée automatiquement une **commande** (source Vitrine) avec les plats pré-commandés : la cuisine la voit et elle compte dans les ventes et les statistiques (étape 3), avec les noms des convives en note des lignes.
- **E-mails** : *(Claude)* hors périmètre. La confirmation et le lien d'annulation sont affichés à l'écran, et un `IEmailSender` journalise pour l'instant.

## 4. Paiement *(Claude)*

- **Interface** : `IPaymentGateway` (créer un paiement avec montant, description, URL de retour et URL de webhook ; lire son statut ; rembourser).
- **Mode Demo** (par défaut en développement) : une page de paiement simulée dans la vitrine (« Payer » / « Échec ») qui met à jour le statut. Aucune clé, aucun argent.
- **Mode Mollie** : API REST v2 via `HttpClient`, adaptée à la Belgique (Bancontact, cartes, Payconiq).
  - La clé `Mollie:ApiKey` est lue dans les user-secrets ou une variable d'environnement, jamais dans `appsettings`.
  - On utilise uniquement la clé de test `test_…` en développement.
  - Un webhook `/webhooks/mollie` est prévu, et la page de retour relit aussi le statut, car le webhook n'est pas joignable en local.
- On n'enregistre jamais de données de carte : seul l'id du paiement chez la passerelle est stocké.

## 5. Carte des vins

- **Bibliothèque partagée** (`WineReference`, commune à tous les restaurants, en lecture seule pour eux) d'environ 60 vins courants (France, Belgique, Italie, Espagne…).
  - Chaque fiche contient : nom, appellation, région, pays, couleur, cépages, robe, nez, bouche, garde, température de service et accords généraux.
  - Elle est livrée sous forme de fichier JSON intégré et chargée au démarrage de façon idempotente.
- **Carte du restaurant** (`WineListItem`) : vin choisi dans la bibliothèque, dont la fiche est copiée pour pouvoir être modifiée, ou vin saisi à la main. On y ajoute le millésime, le producteur, le prix bouteille, le prix verre facultatif, le mot du restaurateur, les plats du restaurant en accord, et les statuts publié ou brouillon.
- **Brouillon assisté** :
  - pour un vin saisi à la main, « Rédiger la fiche » appelle Claude (`claude-opus-5-5`, sortie structurée), qui reçoit le nom, le millésime, le producteur et la liste des plats publiés du restaurant ;
  - le brouillon remplit le formulaire, que le restaurateur relit et publie ;
  - en mode Demo, un texte générique est proposé ;
  - sans clé API, un message invite à rédiger à la main.

## 6. Réglages vitrine (back, `/vitrine`)

- **Publication** : publiée ou non.
- **Contenu** : thème (phrase), texte d'ambiance, photo de couverture, galerie (téléversement de JPEG, PNG ou WebP de 5 Mo maximum, réduits dans le navigateur comme les tickets), couleur d'accent.
- **Réservation** :
  - services et capacités, fermetures, délais ;
  - seuil des grands groupes, acompte par personne, pourcentage de prépaiement, délai d'annulation gratuite ;
  - réservations ouvertes ou non.
- **Coordonnées et fuseau horaire** du restaurant.
- **Plats** : chaque plat reçoit une **description publique** et une case « Afficher sur la vitrine », modifiables dans l'éditeur de recettes.

## 7. Hors périmètre

E-mails et SMS réels, plan de tables graphique, liste d'attente, avis clients, commande à emporter en ligne, multi-langue, nom de domaine par restaurant.

## 8. Terminé quand

- En mode Demo, avec le Petit Bistro publié : le visiteur voit l'ambiance, le menu et les vins. Il réserve pour 2 avec un acompte, puis pour 8 avec les plats et les noms de chacun et un prépaiement. Le paiement simulé confirme la réservation, et la place disparaît des disponibilités.
- Le chef voit la réservation dans le back ; « Arrivé » crée la commande en cuisine.
- Le client annule via son lien, avec la règle de remboursement appliquée.
- `dotnet test` passe et le build ne produit aucun avertissement.
