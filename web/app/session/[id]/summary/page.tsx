import { Suspense } from "react";
import Summary from "@/components/Summary";

export default function SummaryPage({ params }: PageProps<"/session/[id]/summary">) {
  return <Suspense fallback={null}>{params.then(({ id }) => <Summary id={id} />)}</Suspense>;
}
