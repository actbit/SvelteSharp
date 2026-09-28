import type { ViewProps } from "dotnet:rendering";

type BenchmarkModel = {
  title: string;
  items: string[];
};

export default function Benchmark({ model }: ViewProps<BenchmarkModel>) {
  return (
    <main>
      <h1>{model.title}</h1>
      <ul>
        {model.items.map(item => <li key={item}>{item}</li>)}
      </ul>
    </main>
  );
}
