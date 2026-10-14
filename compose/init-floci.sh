#!/bin/sh
set -eu

topic_name="waste_obligations_analytics_events"
queue_name="waste_obligations_notifications_analytics_events_queue"
command_queue_name="waste_obligations_notifications_commands.fifo"
command_dead_letter_queue_name="waste_obligations_notifications_commands_dlq.fifo"

topic_arn="$(aws sns create-topic --name "$topic_name" --query TopicArn --output text)"
queue_url="$(aws sqs create-queue --queue-name "$queue_name" --query QueueUrl --output text)"
queue_arn="$(
    aws sqs get-queue-attributes \
        --queue-url "$queue_url" \
        --attribute-names QueueArn \
        --query 'Attributes.QueueArn' \
        --output text
)"

cat > /tmp/queue-attributes.json <<EOF
{
  "Policy": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"sqs:SendMessage\",\"Resource\":\"$queue_arn\",\"Condition\":{\"ArnEquals\":{\"aws:SourceArn\":\"$topic_arn\"}}}]}"
}
EOF

aws sqs set-queue-attributes --queue-url "$queue_url" --attributes file:///tmp/queue-attributes.json
aws sns subscribe \
    --topic-arn "$topic_arn" \
    --protocol sqs \
    --notification-endpoint "$queue_arn" \
    --attributes RawMessageDelivery=true > /dev/null

command_dead_letter_queue_url="$(
    aws sqs create-queue \
        --queue-name "$command_dead_letter_queue_name" \
        --attributes FifoQueue=true \
        --query QueueUrl \
        --output text
)"
command_dead_letter_queue_arn="$(
    aws sqs get-queue-attributes \
        --queue-url "$command_dead_letter_queue_url" \
        --attribute-names QueueArn \
        --query 'Attributes.QueueArn' \
        --output text
)"
command_queue_url="$(
    aws sqs create-queue \
        --queue-name "$command_queue_name" \
        --attributes FifoQueue=true,ContentBasedDeduplication=false,VisibilityTimeout=120 \
        --query QueueUrl \
        --output text
)"

cat > /tmp/command-queue-attributes.json <<EOF
{
  "RedrivePolicy": "{\\"deadLetterTargetArn\\":\\"$command_dead_letter_queue_arn\\",\\"maxReceiveCount\\":\\"3\\"}"
}
EOF

aws sqs set-queue-attributes --queue-url "$command_queue_url" --attributes file:///tmp/command-queue-attributes.json

# A separate topic and queues belong only to the Docker submission scenario.
e2e_topic_arn="$(aws sns create-topic --name submission_e2e_analytics --query TopicArn --output text)"
e2e_queue_url="$(aws sqs create-queue --queue-name submission_e2e_analytics_queue --query QueueUrl --output text)"
e2e_queue_arn="$(aws sqs get-queue-attributes --queue-url "$e2e_queue_url" --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)"
cat > /tmp/e2e-queue-attributes.json <<EOF
{
  "Policy": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"sqs:SendMessage\",\"Resource\":\"$e2e_queue_arn\",\"Condition\":{\"ArnEquals\":{\"aws:SourceArn\":\"$e2e_topic_arn\"}}}]}"
}
EOF
aws sqs set-queue-attributes --queue-url "$e2e_queue_url" --attributes file:///tmp/e2e-queue-attributes.json
aws sns subscribe --topic-arn "$e2e_topic_arn" --protocol sqs --notification-endpoint "$e2e_queue_arn" --attributes RawMessageDelivery=true > /dev/null
aws sqs create-queue --queue-name submission_e2e_commands.fifo --attributes FifoQueue=true,ContentBasedDeduplication=false,VisibilityTimeout=120 > /dev/null
aws sqs create-queue --queue-name submission_e2e_commands_dlq.fifo --attributes FifoQueue=true > /dev/null

echo "Ready: $queue_name is subscribed to $topic_name; $command_queue_name and $command_dead_letter_queue_name are ready"

touch /tmp/floci-ready
exec tail -f /dev/null
