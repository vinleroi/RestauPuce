# Étape 3 — décisions prises et points à reprendre

L'utilisateur a demandé de passer directement au code. Les choix de design sont donc ceux de Claude ; ils sont décrits dans `specs/2026-11-03-ventes-stats-design.md`. Ce document liste les arbitrages faits en cours d'implémentation et les remarques reportées.

## Décisions prises en cours de route

- **Menu engineering.** Le seuil de popularité est calculé sans division (`qté × n ≥ 0,70 × total`), ce qui rend les égalités exactes. Les marges sont arrondies à 6 décimales avant le classement. Une recette incomplète ou sans ligne est exclue de la matrice.
- **Ventes saisies.** Une vente garde le prix et la TVA figés à sa première saisie : une correction ne change que la quantité. Un total de semaine est toujours enregistré en « Journée ». Les quantités sont plafonnées à 1 000 000 en vente et à 1 000 par ligne de commande.
- **Commandes.**
  - Numérotation séquentielle par restaurant, avec nouvelle tentative en cas de conflit.
  - « Avancer » et « Annuler » vérifient le statut affiché (`expectedFrom`) : une carte périmée est refusée.
  - Les commandes encore ouvertes des jours précédents restent sur le tableau.
  - La note de commande s'affiche sur la carte en cuisine.
- **Inventaires.**
  - L'enregistrement n'envoie que les lignes modifiées et celles vidées explicitement : deux appareils peuvent compter en parallèle.
  - La clôture se fait par mise à jour conditionnelle.
  - Un ingrédient présent dans un inventaire clôturé ne peut plus être supprimé ; il faut l'archiver.
- **Suppression de recette.** Elle est refusée si la recette figure dans des commandes, des ventes ou des pertes ; il faut l'archiver.
- **Statistiques.**
  - Les coûts utilisent les prix en vigueur à la fin de la période ; les pertes, ceux de leur date.
  - L'écart d'inventaire n'est calculé que sur les ingrédients comptés aux deux inventaires. Les autres sont listés comme « non comptés ».
  - Les achats sont les tickets validés, à la date `ReceiptDate ?? CreatedAt`.
  - Le food cost réel est marqué « (partiel) » si un ingrédient non compté a bougé, et « (incomplet) » si un prix manque.
  - Une recette sans ligne est marquée incomplète, avec un avertissement.
- **Données de démo.**
  - L'inventaire de clôture est daté du jour du seed. L'écart n'est donc visible avec la période par défaut que ce jour-là ; ensuite, utiliser le raccourci « Entre les deux derniers inventaires ».
  - Le food cost réel de démo apparaît « (partiel) », parce que les herbes ne sont pas comptées : c'est le garde-fou qui fonctionne.

## À reprendre (non bloquant)

- **Recette sans ligne.**
  - Elle est encore chiffrée à 0 € « complet » dans les pertes (`WasteCosting`), dans la liste des recettes et dans la moyenne du tableau de bord.
  - Déplacer la règle « sans ligne = coût inconnu » dans `RecipeBook` / `CostResult.IsComplete` pour que tous les écrans soient d'accord.
- **Concurrence.**
  - Deux éditions simultanées de la même commande : la dernière écrite l'emporte. Ajouter un jeton de version (`xmin`).
- **Fuseau horaire.**
  - Les journées sont en UTC et l'affichage en heure locale du serveur.
  - Avant la production : un fuseau horaire par restaurant (Europe/Brussels), et un `TimeProvider` pour les services et le seed. Les tests du seed dépendent aujourd'hui de la date du jour.
- **Ergonomie.**
  - Période de la liste des pertes non protégée contre les chargements concurrents.
  - Coût de rendu de la grille des ventes sur une grande carte.
  - La liste des pertes ne distingue pas un plat d'une préparation quand l'unité est « pièce ».
  - Étiquette de semaine qui n'utilise pas `Ui.Fr`.
- **Code.**
  - `StatsPage.razor` fait environ 600 lignes : extraire la section écart et la matrice.
  - Le fragment `Kpi` est dupliqué entre le tableau de bord et les stats.
  - Les fichiers de tests `StatsServiceTests` et `OrderServiceTests` sont longs.
- **Performance.** Les chargements sur la fenêtre d'écart ]A, B] ne sont pas bornés. `COALESCE` empêche l'index sur `ReceiptDate`.
