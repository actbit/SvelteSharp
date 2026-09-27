<script lang="ts">
  import { onMount } from 'svelte';
  import type { HomePage } from '@sveltesharp/models';

  let { model }: { model: HomePage } = $props();
  let hydrated = $state(false);
  let clicks = $state(0);

  // onMountはSSRでは実行されず、ブラウザーでhydrateされた後にだけ実行されます。
  onMount(() => {
    hydrated = true;
  });
</script>

<svelte:head>
  <title>{model.title}</title>
</svelte:head>

<main>
  <h1>{model.title}</h1>
  <p id="render-phase">
    {hydrated ? 'Browser: hydrate completed' : 'Server: initial HTML rendered'}
  </p>

  <button id="hydration-counter" type="button" onclick={() => clicks += 1}>
    Hydrated clicks: {clicks}
  </button>

  <ul>
    {#each model.features as feature}
      <li>{feature}</li>
    {/each}
  </ul>
</main>
