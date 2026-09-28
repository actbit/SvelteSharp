<script lang="ts">
  type BenchmarkModel = { title: string; items: string[] };
  let { model }: { model: BenchmarkModel } = $props();
</script>

<main>
  <h1>{model.title}</h1>
  <ul>
    {#each model.items as item}
      <li>{item}</li>
    {/each}
  </ul>
</main>
