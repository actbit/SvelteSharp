<script lang="ts">
  import type { HomePage } from '@sveltesharp/models';

  let { model }: { model: HomePage } = $props();
</script>

<svelte:head>
  <title>{model.title}</title>
</svelte:head>

<main>
  <h1>{model.title}</h1>
  <ul>
    {#each model.features as feature}
      <li>{feature}</li>
    {/each}
  </ul>
</main>
