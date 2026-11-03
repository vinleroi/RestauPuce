# Étape 2 — décisions prises et points à reprendre

L'utilisateur a demandé de passer directement au code. Tous les choix de design de l'étape 2 sont donc ceux de Claude ; ils sont décrits dans la spec `specs/2026-11-03-scan-tickets-design.md`. Ce document liste les arbitrages faits en cours d'implémentation et les remarques de relecture volontairement reportées.

## Décisions prises en cours de route

- `ReceiptService` ne reçoit ni `ITenantContext` ni `IngredientService` : le contexte filtré suffit à isoler les restaurants.
- Le fallback serveur en cas de refus est activé sous la forme `fallbacks: "default"` (SDK Anthropic 13.4.0).
- Rapprochement d'ingrédient :
  - les mots vides (de, la, du…) et les mots contenant un chiffre sont ignorés ;
  - à score égal, l'ingrédient le plus précis l'emporte ;
  - une correspondance par nom est affichée « à confirmer », une correspondance mémorisée « mémorisé ».
- « FILET DE BOEUF » contre « Filet de poulet » reste une correspondance par nom (score 0,5), affichée « à confirmer ».
- La validation d'un ticket est atomique. Le ticket est d'abord réservé (passage `Extracted`→`Applied` dans la transaction), puis prix, historique, lignes et correspondances sont écrits en une seule transaction. Un double clic ou deux onglets ne peuvent donc pas l'appliquer deux fois.
- Un ticket est « plus ancien » à la journée près (UTC). Une entrée qui remplace le prix courant devient toujours la dernière de l'historique (+1 µs si nécessaire).
- Lecture IA :
  - un taux de TVA lu hors [0, 1] est traité comme inconnu ;
  - les valeurs trop grandes pour les colonnes deviennent `null` ;
  - une date lue dans le futur (après demain) devient `null`, et une date future saisie est refusée.
- Les photos sont réduites dans le navigateur avant l'envoi (2000 px, JPEG 0,85), car l'API refuse les images de plus de 5 Mo.
- Client Anthropic : 1 nouvelle tentative maximum, délai de 5 minutes. Messages d'erreur en français ; les échecs de lecture sont journalisés (`LogWarning`).
- Validation refusée si aucune ligne n'est à appliquer.

## À reprendre (non bloquant)

- **Concurrence**
  - Ajouter un jeton `xmin` sur `Receipt` : une relecture, un enregistrement de brouillon ou une suppression peuvent encore croiser une validation faite dans un autre onglet.
  - Deux validations simultanées touchant le même ingrédient peuvent écrire la même date d'historique (départagée par l'id).
- **Prix du jour**
  - Une modification manuelle de prix faite un jour où un ticket daté du lendemain a été appliqué laisse le prix courant et la dernière entrée décalés jusqu'au lendemain.
- **TVA**
  - Aucun rappel avant de valider des lignes « TVA à vérifier ».
  - Pas de choix du taux dans l'écran : le total HT se corrige à la main.
- **Factures longues** : passer en streaming avec plus de jetons de sortie si de longues factures (50 à 100 lignes) échouent.
- **Limites**
  - La limite de 5 Mo de l'API porte probablement sur la taille en base64.
  - Pas de plafond sur les quantités et montants saisis dans le brouillon.
- **Aperçu**
  - L'aperçu charge tous les octets du fichier pour choisir entre `<img>` et `<iframe>` : ajouter `GetFileInfoAsync`.
  - Pas d'ETag ni de CSP sur l'endpoint du fichier.
- **Mobile**
  - Une très grande photo est décodée en pleine résolution avant d'être réduite.
  - Non testé sur un vrai téléphone (Safari, HEIC, rotation EXIF).
- **Tests**
  - Ajouter un test `WebApplicationFactory` pour `/receipts/{id}/file` (voir `etape-1-suivi.md`).
  - Pas de test de parité d'arrondi dans `ReceiptReviewForm.Evaluate`.
  - Pas de test de bord à -5 % ni pour « pas de restaurant → liste vide ».
- **Code**
  - `ReceiptReview.razor` fait environ 650 lignes : extraire le tableau des lignes et le récapitulatif.
  - `PriceVariation` (Shared) dépend de `ReceiptReviewForm.LevelOf` : déplacer `LevelOf` dans `Ui`.
  - La liste des variations du tableau de bord n'est pas limitée.
- **Rapprochement** : « Farine T55 » et « T45 » sont indiscernables par le nom ; la correspondance mémorisée prend le relais après la première validation.
- **À vérifier en premier** : le test avec un vrai ticket et une vraie clé API. Le schéma JSON et le beta de fallback n'ont pas encore été confirmés en conditions réelles.
